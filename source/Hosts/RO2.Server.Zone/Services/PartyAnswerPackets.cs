using System;
using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Request envelopes and response bodies for the party requests (1384, 1385, 1388, 1504, 1529).
/// </summary>
/// <remarks>
/// <para>
/// Every request here except 1504 carries the client's msgTag envelope. The blob has the layout
/// <c>[u16 innerSize == blob length][u16 msgTag][u32 sender PC handle][payload at +8]</c>, and the result
/// acks echo the sender handle.
/// </para>
/// </remarks>
public static class PartyAnswerPackets
{
    // Client msgTags (u16 at request blob+2).
    public const ushort MsgTagReqPartyOut = 58144;             // RMI 1384, 0xE320, blob 8
    public const ushort MsgTagReqPartyChangeItemRule = 58146;  // RMI 1385, 0xE322, blob 18
    public const ushort MsgTagReqPartyExpulsion = 58152;       // RMI 1388, 0xE328, blob 16
    public const ushort MsgTagReqPartyTacticsTarget = 58439;   // RMI 1529, 0xE447, blob 14 (no compact prefix: the body is the blob)
    public const ushort MsgTagReqPartyDiceStart = 58255;       // RMI 1379, 0xE38F, blob 20
    public const ushort MsgTagReqPartyDiceJoin = 58257;        // RMI 1380, 0xE391, blob 19

    // Server msgTags (u16 at answer blob+2).
    public const ushort MsgTagOnPartyExpulsion = 58153;        // RMI 2353, 0xE329
    public const ushort MsgTagOnPartyNotifyChangeItem = 58167; // RMI 2363, 0xE337
    public const ushort MsgTagOnPartyNotifyExpulsion = 58170;  // RMI 2366, 0xE33A
    public const ushort MsgTagAnsPartyTacticsTarget = 58437;   // RMI 2751, 0xE445

    /// <summary>Value of a reserved dword (answer blob+4) that the client does not read.</summary>
    private const uint ReservedZero = 0;

    /// <summary>Actor type tag in the low byte of a party handle (PC handles carry 0x11, NPC handles 0x13).</summary>
    private const uint PartyActorTag = 0x15;

    /// <summary>Bit width of the actor type tag; the party id sits above it.</summary>
    private const int ActorHandleTagBits = 8;

    /// <summary>Request blob offset of the u32 sender PC handle (after the u16 innerSize and the u16 msgTag).</summary>
    private const int SenderHandleOffset = 4;

    /// <summary>Smallest request blob: innerSize, msgTag and the sender handle.</summary>
    private const int MinRequestBlobLen = 8;

    /// <summary>Entry count of a 2751 that carries the one entry a 1529 changed.</summary>
    private const byte SingleTargetEntry = 1;

    // Writer capacities: each answer body is a 1-byte compact marker + a 1-byte length + the blob.
    private const int ResultAckBodyLen = 12;               // blob 10
    private const int NotifyChangeItemBodyLen = 24;        // blob 22
    private const int NotifyExpulsionBodyLen = 22;         // blob 20
    private const int FrameMoveBodyLen = 28;               // blob 26
    private const int AnsTacticsTargetBodyLen = 21;        // blob 19

    /// <summary>Master looter GID of 2363, always written as zero (no master looter is named).</summary>
    private const ulong NoMasterLooterGid = 0;

 /// <summary>
 /// Strips the compact-length prefix <c>[u8 width in {1,2,4,8}][width bytes LE = N]</c> and returns the N-byte blob.
 /// </summary>
 /// <returns>False when the body is not a well-formed compact ByteArray.</returns>
 /// <remarks>
 /// The length must match the number of bytes that follow exactly. The envelope itself is defined in
 /// <see cref="CompactEnvelope"/>.
 /// </remarks>
    public static bool TryGetCompactBlob(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> blob)
        => body.TryGetCompactBlob(CompactLengthCheck.Strict, out blob);

 /// <summary>
 /// Checks the request envelope (innerSize equals the blob length, the expected msgTag, and at least
 /// <paramref name="minLen"/> bytes) and returns the sender handle at blob+4.
 /// </summary>
    public static bool TryReadEnvelope(ReadOnlySpan<byte> blob, ushort msgTag, int minLen, out uint senderHandle)
    {
        senderHandle = 0;
        if (blob.Length < Math.Max(MinRequestBlobLen, minLen)) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(blob) != blob.Length) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]) != msgTag) return false;
        senderHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob[SenderHandleOffset..]);
        return true;
    }

 /// <summary>
 /// The party handle carried by roster broadcasts. This is the only definition; the party service uses it so that
 /// the roster and tactics messages always name the same party.
 /// </summary>
    public static uint PartyHandle(PartyState party) => (party.PartyId << ActorHandleTagBits) | PartyActorTag;

 /// <summary>
 /// The member GID carried by roster records. This is the only definition, so the GIDs the client sends back
 /// in 1388 and 1504 match the ones the handlers look up.
 /// </summary>
    public static ulong MemberGid(PlayerState? p)
        => p == null ? 0UL : (p.CharacterUid != 0 ? p.CharacterUid : p.CharacterId);

 /// <summary>2353 result: the target is not a member of the kicker's party.</summary>
    public const short ResultExpulsionNotMember = 15712;

 /// <summary>2352 result: a leader on a war-of-emperium map tried to delegate to a member on another map.</summary>
    public const short ResultChangeMasterWoeMap = 24324;

 /// <summary>
 /// Builds the result ack for the requester of 2349, 2350, 2352 or 2353: the blob
 /// <c>[u16 10][u16 msgTag][u32 handle][i16 result]</c>. The client shows an error only for a non-zero result.
 /// </summary>
 /// <param name="msgTag">msgTag of the ack opcode.</param>
 /// <param name="handle">Sender handle echoed from the request.</param>
 /// <param name="result">0 for success, or one of the refusal codes such as <see cref="ResultExpulsionNotMember"/>.</param>
    public static byte[] BuildResultAck(ushort msgTag, uint handle, short result = 0)
    {
        using var pw = new PacketWriter(ResultAckBodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            p.WriteUInt32(handle);
            p.WriteInt16(result);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2363 OnPartyNotifyChangeItem, sent to every member after a 1385 request: the blob
 /// <c>[u16 22][u16 58167][u32 0][u32 party handle][u8 lootMode][u8 quality][u64 masterLooterGid]</c>.
 /// The master looter GID is written as zero.
 /// </summary>
    public static byte[] BuildPartyNotifyChangeItem2363(uint partyHandle, byte lootMode, byte quality)
    {
        using var pw = new PacketWriter(NotifyChangeItemBodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagOnPartyNotifyChangeItem);
            p.WriteUInt32(ReservedZero);
            p.WriteUInt32(partyHandle);
            p.WriteByte(lootMode);
            p.WriteByte(quality);
            p.WriteUInt64(NoMasterLooterGid);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2366 OnPartyNotifyExpulsion: the blob <c>[u16 20][u16 58170][u32 0][u32 party handle][u64 expelled GID]</c>.
 /// The client removes the member with that GID.
 /// </summary>
    public static byte[] BuildPartyNotifyExpulsion2366(uint partyHandle, ulong expelledGid)
    {
        using var pw = new PacketWriter(NotifyExpulsionBodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagOnPartyNotifyExpulsion);
            p.WriteUInt32(ReservedZero);
            p.WriteUInt32(partyHandle);
            p.WriteUInt64(expelledGid);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2705 NfyPartyFrameMove: the blob <c>[u16 26][u16 58434][u32 0][u64 gid1][u8 slot1][u64 gid2][u8 slot2]</c>.
 /// Each slot is the frame position of the member with the matching GID. <c>gid2</c> is zero exactly when
 /// <c>slot2</c> is zero (a move into an empty slot).
 /// </summary>
    public static byte[] BuildNfyPartyFrameMove2705(ulong gid1, byte slot1, ulong gid2, byte slot2)
    {
        using var pw = new PacketWriter(FrameMoveBodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(PartyStatePackets.MsgTagNfyPartyFrameMove);
            p.WriteUInt32(ReservedZero);
            p.WriteUInt64(gid1);
            p.WriteByte(slot1);
            p.WriteUInt64(gid2);
            p.WriteByte(slot2);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2751 AnsPartyTacticsTarget with a single entry: the blob
 /// <c>[u16 19][u16 58437][u32 0][u32 mapId][u8 count=1][u16 mark][u32 target handle]</c>. A mark of 0 clears the target.
 /// </summary>
    public static byte[] BuildAnsPartyTacticsTarget2751(uint mapId, ushort mark, uint targetHandle)
    {
        using var pw = new PacketWriter(AnsTacticsTargetBodyLen);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAnsPartyTacticsTarget);
            p.WriteUInt32(ReservedZero);
            p.WriteUInt32(mapId);
            p.WriteByte(SingleTargetEntry);
            p.WriteUInt16(mark);
            p.WriteUInt32(targetHandle);
        });
        return pw.ToArray();
    }
}
