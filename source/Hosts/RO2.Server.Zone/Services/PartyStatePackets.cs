using System;
using RO2.Network.Packets;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Byte-exact server-to-client body builders for the party-state notifications.
/// </summary>
/// <remarks>
/// <para>
/// Most opcodes here use the compact-length byte-array envelope:
/// <c>[compactLen(selfLen)][u16 selfLen][u16 msgTag][fields]</c>, with <c>selfLen = 2 + 2 + fields.Length</c>.
/// The "blob" in the field offsets below is the byte array after the compact length, so the fields start at
/// <c>blob+4</c>. The first body byte must be a valid compact-length marker (1, 2, 4 or 8), otherwise the
/// client decodes an empty array and dereferences it.
/// </para>
/// <para>
/// Each opcode has a constant msgTag in the band 52001..58526. The party request/answer pairs run in
/// lockstep: the C2S request with tag N is answered by the S2C opcode with tag N+1 (1384/2349, 1385/2350,
/// 1386/2351, 1387/2352, 1388/2353).
/// </para>
/// <para>
/// A few answers are not enveloped: they are a plain compact-length blob with no message tag, or a fixed-size
/// raw body. Those are marked on their builders and must never be wrapped.
/// </para>
/// </remarks>
public static class PartyStatePackets
{
    // msgTags (u16 at blob+2), one per enveloped opcode.
    public const ushort MsgTagOnPartyInvite = 58140;             // RMI 2346
    public const ushort MsgTagOnPartyJoin = 58141;               // RMI 2347
    public const ushort MsgTagOnPartyError = 58143;              // RMI 2348
    public const ushort MsgTagOnPartyOut = 58145;                // RMI 2349
    public const ushort MsgTagOnPartyChangeItem = 58147;         // RMI 2350
    public const ushort MsgTagOnPartyChangeExp = 58149;          // RMI 2351 (the free value between 2350 and 2352)
    public const ushort MsgTagOnPartyChangeMaster = 58151;       // RMI 2352
    public const ushort MsgTagOnPartyNotifyChangeMaster = 58169; // RMI 2365
    public const ushort MsgTagNfyPartyTypeChange = 58436;        // RMI 2692
    public const ushort MsgTagNfyPartyFightingTrimStart = 58432; // RMI 2699
    public const ushort MsgTagNfyPartySubMaster = 58433;         // RMI 2703
    public const ushort MsgTagNfyPartyFrameMove = 58434;         // RMI 2705
    public const ushort MsgTagNfyPartyDismissSubmaster = 58435;  // RMI 2707
    public const ushort MsgTagNfyPartyDiceChattingCmd = 58478;   // RMI 2788

    /// <summary>Characters in an inline character-name field (UTF-16LE, 42 bytes).</summary>
    private const int NameChars = 21;

    /// <summary>Value of a reserved dword or word that the client does not read (and the "success" result of an answer).</summary>
    private const uint ReservedZero = 0;

    /// <summary>Result 0 of an answer: the request was taken, the client shows no error.</summary>
    private const ushort ResultOk = 0;

    /// <summary>Wraps fields in the compact-length envelope: compact length, self size, message tag, then the fields.</summary>
    private static byte[] Wrap(ushort msgTag, Action<PacketWriter> fields)
    {
        using var pw = new PacketWriter(64);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            fields(p);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds 2346 (OnPartyInvite): the inviter's answer to its own 1382 request, sent when the invitation is
    /// settled (the invitation dialog is 2347, sent to the target). Blob 53, fields:
    /// <list type="bullet">
    /// <item><description>blob+4 u32 the inviter's own PC handle, echoed from the 1382.</description></item>
    /// <item><description>blob+8 i16 result: 0 when the target accepted, otherwise a party error code.</description></item>
    /// <item><description>blob+10 wchar_t[21] the target's name.</description></item>
    /// <item><description>blob+52 u8 the party type at answer time (0 normal, 2 raid).</description></item>
    /// </list>
    /// </summary>
    public static byte[] BuildOnPartyInvite(uint inviterHandle, short result, string targetName, byte partyType)
        => Wrap(MsgTagOnPartyInvite, p =>
        {
            p.WriteUInt32(inviterHandle);                // blob+4
            p.WriteInt16(result);                        // blob+8
            p.WriteBytes(FixedWide(targetName, NameChars)); // blob+10..51
            p.WriteByte(partyType);                      // blob+52
        });

    /// <summary>
    /// Builds 2347 (OnPartyJoin): the invitation dialog sent to the target of a 1382 request. Blob 59, fields:
    /// <list type="bullet">
    /// <item><description>blob+4 u32 the target's own PC handle.</description></item>
    /// <item><description>blob+8 u64 the inviter's character GID.</description></item>
    /// <item><description>blob+16 u8 the inviter's party type (0 normal, 2 raid).</description></item>
    /// <item><description>blob+17 wchar_t[21] the inviter's name.</description></item>
    /// </list>
    /// </summary>
    public static byte[] BuildOnPartyJoin(uint targetHandle, ulong inviterGid, byte partyType, string inviterName)
        => Wrap(MsgTagOnPartyJoin, p =>
        {
            p.WriteUInt32(targetHandle);                 // blob+4
            p.WriteUInt64(inviterGid);                   // blob+8
            p.WriteByte(partyType);                      // blob+16
            p.WriteBytes(FixedWide(inviterName, NameChars)); // blob+17..58
        });

    // PartyMember208 stat block, record-relative; shared by 2357 / 2359 / 2361. The record is 208 bytes.
    // PartyNotifyPackets.BuildMemberRecord fills only handle, GID and name; the party window reads these
    // offsets for the member row (level, class, HP/SP bars, map icon, raid frame slot).
    private const int RecLevelOff = 15;      // u8   level
    private const int RecClassOff = 16;      // i16  class, the same value the self row carries
    private const int RecCurHpOff = 19;      // u32  current HP (never above max HP)
    private const int RecMaxHpOff = 23;      // u32  max HP
    private const int RecCurSpOff = 27;      // u32  current SP (never above max SP)
    private const int RecMaxSpOff = 31;      // u32  max SP
    private const int RecMapIdOff = 35;      // u32  map id; the world map draws a party icon only when it equals the viewer's map
    private const int RecPosOff = 41;        // 3 x f32 world x/y/z
    private const int RecFrameSlotOff = 62;  // u8   1-based frame slot; the raid view groups members by (slot-1)/5

    /// <summary>Mask applied to the job id to get the class slot of the member record (same mask as the packed-appearance class slot).</summary>
    private const int ClassSlotMask = 0x1F;

    /// <summary>Offset of the y component from <see cref="RecPosOff"/>, in bytes.</summary>
    private const int PosYStride = 4;

    /// <summary>Offset of the z component from <see cref="RecPosOff"/>, in bytes.</summary>
    private const int PosZStride = 8;

    /// <summary>
    /// Builds a 208-byte party member record with the identity fields and the stat block the party window
    /// reads. Fields with no source stay zero.
    /// </summary>
    public static byte[] BuildPartyMemberRecord(uint actorHandle, ulong characterGid, string name,
        byte level, byte job, uint curHp, uint maxHp, uint curSp, uint maxSp, uint mapId,
        float x, float y, float z, byte frameSlot)
    {
        var rec = PartyNotifyPackets.BuildMemberRecord(actorHandle, characterGid, name);
        var s = rec.AsSpan();
        s[RecLevelOff] = level;
        // Masked like the packed-appearance class slot, so a member row and the self row of the same character match.
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(s[RecClassOff..], (short)(job & ClassSlotMask));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[RecCurHpOff..], Math.Min(curHp, maxHp));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[RecMaxHpOff..], maxHp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[RecCurSpOff..], Math.Min(curSp, maxSp));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[RecMaxSpOff..], maxSp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[RecMapIdOff..], mapId);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s[RecPosOff..], x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s[(RecPosOff + PosYStride)..], y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s[(RecPosOff + PosZStride)..], z);
        s[RecFrameSlotOff] = frameSlot;
        return rec;
    }

    /// <summary>Encodes a string as UTF-16LE in a fixed-width, null-padded buffer of <paramref name="maxChars"/> characters.</summary>
    private static byte[] FixedWide(string? value, int maxChars)
    {
        var buf = new byte[maxChars * 2];
        if (!string.IsNullOrEmpty(value))
        {
            var take = Math.Min(value.Length, maxChars);
            System.Text.Encoding.Unicode.GetBytes(value.AsSpan(0, take), buf.AsSpan(0, take * 2));
        }
        return buf;
    }

    /// <summary>
    /// Builds 2348 (OnPartyError): the target's answer to its own 1383 request when the join did not happen.
    /// Blob 53, fields: blob+4 u32 the target's own PC handle, blob+8 i16 error code, blob+10 u8 zero,
    /// blob+11 wchar_t[21] the inviter's name as the 1383 carried it.
    /// </summary>
    public static byte[] BuildOnPartyError(uint targetHandle, short code, string inviterName)
        => Wrap(MsgTagOnPartyError, p =>
        {
            p.WriteUInt32(targetHandle);                 // blob+4
            p.WriteInt16(code);                          // blob+8
            p.WriteByte((byte)ReservedZero);             // blob+10
            p.WriteBytes(FixedWide(inviterName, NameChars)); // blob+11..52
        });

    /// <summary>
    /// Builds 2352 (OnPartyChangeMaster): the requester's acknowledgment of its 1387 leader-change request
    /// (the leader-change notice itself is 2365). The client does nothing on result 0. Same 10-byte
    /// result-ack blob as 2349 / 2350 / 2353.
    /// </summary>
    public static byte[] BuildOnPartyChangeMasterAck(uint requesterHandle)
        => PartyAnswerPackets.BuildResultAck(MsgTagOnPartyChangeMaster, requesterHandle);

    /// <summary>
    /// Builds 2351 (OnPartyChangeExp): the requester's acknowledgment of its 1386 experience-rule change
    /// request, the sibling of the 1385 acknowledgment (2350). Same 10-byte result-ack blob, with the handle
    /// the request carried and result 0; the client reads only the result.
    /// </summary>
    public static byte[] BuildOnPartyChangeExpAck(uint requesterHandle)
        => PartyAnswerPackets.BuildResultAck(MsgTagOnPartyChangeExp, requesterHandle);

    /// <summary>
    /// Builds 2365 (OnPartyNotifyChangeMaster): the new leader, sent to every member. Blob 20:
    /// <c>[u32 0][u32 party handle][u64 new leader GID]</c>. The GID must be a member of the previous roster.
    /// </summary>
    public static byte[] BuildOnPartyNotifyChangeMaster2365(uint partyHandle, ulong newLeaderGid)
        => Wrap(MsgTagOnPartyNotifyChangeMaster, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteUInt32(partyHandle);                  // blob+8
            p.WriteUInt64(newLeaderGid);                 // blob+12
        });

    /// <summary>Opcode of RMI 2358 (OnPartyNotifyDestroy).</summary>
    public const ushort OpcodeOnPartyNotifyDestroy = ZoneS2COpcodes.OnPartyNotifyDestroy;

    /// <summary>Message tag of RMI 2358 (OnPartyNotifyDestroy).</summary>
    public const ushort MsgTagOnPartyNotifyDestroy = 58162;

    /// <summary>
    /// Builds 2358 (OnPartyNotifyDestroy): the party no longer exists. Blob 12:
    /// <c>[u32 0][u32 party handle]</c>; the client ends the party without reading the payload.
    /// </summary>
    public static byte[] BuildOnPartyNotifyDestroy2358(uint partyHandle)
        => Wrap(MsgTagOnPartyNotifyDestroy, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteUInt32(partyHandle);                  // blob+8
        });

    /// <summary>
    /// Builds 2692 (NfyPartyTypeChange), sent to the members other than the requester of a 1497 type change.
    /// Blob 9: <c>[u32 0][u8 party type]</c>.
    /// </summary>
    public static byte[] BuildNfyPartyTypeChange(byte type)
        => Wrap(MsgTagNfyPartyTypeChange, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteByte(type);                           // blob+8
        });

    /// <summary>Builds the 2691 (AnsPartyTypeChange) answer for a taken type change, with result 0.</summary>
    public static byte[] BuildAnsPartyTypeChange(byte type)
        => BuildAnsPartyTypeChange(type, ResultOk);

    /// <summary>
    /// Builds 2691 (AnsPartyTypeChange): a fixed 3-byte raw body <c>[u16 result][u8 party type]</c>. A
    /// non-zero result shows a party error and leaves the party type unchanged on the client.
    /// </summary>
    public static byte[] BuildAnsPartyTypeChange(byte type, ushort result)
        => [(byte)result, (byte)(result >> 8), type];

    /// <summary>Builds a raw 2-byte body holding one u16 result.</summary>
    private static byte[] BuildResultOnly(ushort result)
    {
        using var pw = new PacketWriter(2);
        pw.WriteUInt16(result);
        return pw.ToArray();
    }

    /// <summary>Builds 2702 (AnsPartySubMaster): the answer to a 1503 sub-master request. Raw 2-byte result 0.</summary>
    public static byte[] BuildAnsPartySubMaster2702() => BuildResultOnly(ResultOk);

    /// <summary>
    /// Builds 2703 (NfyPartySubMaster), sent to every member when a sub-master is appointed. Blob 16:
    /// <c>[u32 0][u64 new sub-master character GID]</c>. The GID must be a roster member of every recipient.
    /// </summary>
    public static byte[] BuildNfyPartySubMaster(ulong subMasterGid)
        => Wrap(MsgTagNfyPartySubMaster, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteUInt64(subMasterGid);                 // blob+8
        });

    /// <summary>Builds 2706 (AnsPartyDismissSubMaster): the answer to a 1505 request. Raw 2-byte result 0.</summary>
    public static byte[] BuildAnsPartyDismissSubMaster2706() => BuildResultOnly(ResultOk);

    /// <summary>
    /// Builds 2707 (NfyPartyDismissSubmaster), sent to every member when a sub-master is dismissed. Blob 16:
    /// <c>[u32 0][u64 former sub-master character GID]</c>. The GID must be a roster member of every recipient.
    /// </summary>
    public static byte[] BuildNfyPartyDismissSubmaster(ulong formerSubMasterGid)
        => Wrap(MsgTagNfyPartyDismissSubmaster, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteUInt64(formerSubMasterGid);           // blob+8
        });

    /// <summary>
    /// Builds 2788 (NfyPartyDiceChattingCmd): the party-chat dice roll sent to every member, including the
    /// roller, in answer to a 1545 request. Blob 24, fields: blob+4 u32 zero, blob+8 u16 result (0 = rolled;
    /// 1 and 2 print "input range is too large"), blob+10 u64 the roller's character GID, blob+18 u16 min,
    /// blob+20 u16 max, blob+22 u16 roll.
    /// </summary>
    public static byte[] BuildNfyPartyDiceChattingCmd(ulong rollerGid, ushort min, ushort max, ushort roll)
        => Wrap(MsgTagNfyPartyDiceChattingCmd, p =>
        {
            p.WriteUInt32(ReservedZero);                 // blob+4
            p.WriteUInt16(ResultOk);                     // blob+8
            p.WriteUInt64(rollerGid);                    // blob+10
            p.WriteUInt16(min);                          // blob+18
            p.WriteUInt16(max);                          // blob+20
            p.WriteUInt16(roll);                         // blob+22
        });

    /// <summary>Page number and page count of an empty paged-list answer.</summary>
    private const ushort EmptyListPage = 1;

    /// <summary>Header of a paged-list blob: u16 page, u16 pages, u8 row count.</summary>
    private const int PagedListHeaderBytes = 5;

    /// <summary>
    /// Builds the empty paged-list answer shared by 2621 and 2622: a plain compact-length blob (no message
    /// tag, no self-length echo) holding page 1 of 1 and zero rows, bytes <c>01 05 01 00 01 00 00</c>.
    /// </summary>
    private static byte[] BuildEmptyPagedList()
    {
        using var blob = new PacketWriter(PagedListHeaderBytes);
        blob.WriteUInt16(EmptyListPage);
        blob.WriteUInt16(EmptyListPage);
        blob.WriteByte(0);

        using var pw = new PacketWriter(blob.Length + CompactLengthMaxBytes);
        pw.WriteCompactLength(blob.Length);
        pw.WriteBytes(blob.GetWrittenSpan());
        return pw.ToArray();
    }

    /// <summary>
    /// Builds the empty 2622 (AnsPartyApplySearch) answer, meaning no results. Plain compact-length blob,
    /// never wrapped in the message-tag envelope. The populated form is built from the apply board.
    /// </summary>
    public static byte[] BuildAnsPartyApplySearchEmpty()
        => BuildEmptyPagedList();

    /// <summary>Size of a u32 blob-length prefix.</summary>
    private const int BlobLengthPrefixBytes = 4;

    /// <summary>Size of the u16 member count at the start of the 2778 blob.</summary>
    private const int MemberCountBytes = 2;

    /// <summary>Size of one character GID (u64).</summary>
    private const int GidBytes = 8;

    /// <summary>
    /// Builds 2778 (AnsPartyMemberPCOID) with the GIDs of the party members. u32-prefixed body:
    /// <c>[u32 blobLen][u16 count][count x u64 character GID]</c> with <c>blobLen = 2 + 8 * count</c>.
    /// </summary>
    public static byte[] BuildAnsPartyMemberPCOID(IReadOnlyList<ulong> memberGids)
    {
        var count = Math.Min(memberGids.Count, ushort.MaxValue);
        using var pw = new PacketWriter(BlobLengthPrefixBytes + MemberCountBytes + GidBytes * count);
        pw.WriteUInt32((uint)(MemberCountBytes + GidBytes * count));
        pw.WriteUInt16((ushort)count);
        for (var i = 0; i < count; i++)
            pw.WriteUInt64(memberGids[i]);
        return pw.ToArray();
    }

    /// <summary>
    /// Builds the empty 2621 (AnsPartyMemberSearch) answer, meaning no results. Plain compact-length blob,
    /// never wrapped in the message-tag envelope.
    /// </summary>
    public static byte[] BuildAnsPartyMemberSearchEmpty()
        => BuildEmptyPagedList();

    /// <summary>Length of one row of a 2621 answer, in bytes.</summary>
    public const int PartyMemberSearchRowBytes = 103;

    /// <summary>Largest compact length prefix: the width marker plus an 8-byte value.</summary>
    private const int CompactLengthMaxBytes = 9;

    /// <summary>
    /// Builds a populated 2621 (AnsPartyMemberSearch) answer for one page of listings: a plain compact-length
    /// blob <c>[u16 page][u16 pages][u8 n][n x 103-byte row]</c>. Row layout: <see cref="PartyMatchingListing"/>.
    /// Names and titles are NUL-terminated and zero-padded.
    /// </summary>
    public static byte[] BuildAnsPartyMemberSearch(ushort page, ushort pages, IReadOnlyList<PartyMatchingListing> rows)
    {
        // The client's result window has one control per row (PageSize) and does not bound-check the count.
        var count = Math.Min(rows.Count, PartyMatchingBoard.PageSize);
        using var blob = new PacketWriter(PagedListHeaderBytes + PartyMemberSearchRowBytes * count);
        blob.WriteUInt16(page);
        blob.WriteUInt16(pages);
        blob.WriteByte((byte)count);
        for (var i = 0; i < count; i++)
        {
            var r = rows[i];
            blob.WriteByte(r.MemberCount);                                     // row+0
            blob.WriteByte(r.Category);                                        // row+1
            blob.WriteByte(r.Difficulty);                                      // row+2
            blob.WriteUInt32(r.PartyHandle);                                   // row+3
            blob.WriteUInt32(r.Destination);                                   // row+7
            blob.WriteUInt64(r.PosterGid);                                     // row+11
            blob.WriteBytes(FixedWideTerminated(r.PosterName, PartyMatchingBoard.TextChars)); // row+19
            blob.WriteBytes(FixedWideTerminated(r.Title, PartyMatchingBoard.TextChars));      // row+61
        }

        using var pw = new PacketWriter(blob.Length + CompactLengthMaxBytes);
        pw.WriteCompactLength(blob.Length);
        pw.WriteBytes(blob.GetWrittenSpan());
        return pw.ToArray();
    }

    /// <summary>
    /// Like <see cref="FixedWide"/>, but always keeps the last character free for the terminator: the client
    /// reads the text up to a zero character, so a full buffer would run into the next row.
    /// </summary>
    private static byte[] FixedWideTerminated(string? value, int bufferChars)
        => FixedWide(value is { Length: > 0 } && value.Length >= bufferChars ? value[..(bufferChars - 1)] : value,
            bufferChars);

    /// <summary>
    /// Builds 2618 (AnsPartyMatchingMsg): a raw 2-byte body <c>[u8 result][u8 flag]</c>. The client reads only
    /// <paramref name="result"/> and uses it to pick the matching list it reloads (1 = the recruit notices
    /// requested by 1560 and answered by 2621; 2 = the listings requested by 1460 and answered by 2622; any
    /// other value reloads nothing). <paramref name="flag"/> is not read.
    /// </summary>
    public static byte[] BuildAnsPartyMatchingMsg(byte result, byte flag)
        => [result, flag];

    /// <summary>
    /// Builds 2624 (AnsPartyMatchingError): a raw 4-byte body <c>[u16 op][u16 code]</c>. The client shows
    /// nothing when either is 0. The op/code pairs are PartyMatchingDetailPackets.ErrorOp*/Code*.
    /// </summary>
    public static byte[] BuildAnsPartyMatchingError(ushort op, ushort code)
    {
        using var pw = new PacketWriter(4);
        pw.WriteUInt16(op);
        pw.WriteUInt16(code);
        return pw.ToArray();
    }

    /// <summary>Default result of 2830 (AnsFindParty_Party_SelectRole_Cancel).</summary>
    private const ushort SelectRoleCancelDefaultResult = 2;

    /// <summary>
    /// Builds 2830 (AnsFindParty_Party_SelectRole_Cancel): a raw 2-byte u16 result. The client only logs it
    /// and hides the select-role window.
    /// </summary>
    public static byte[] BuildAnsFindPartySelectRoleCancel(ushort result = SelectRoleCancelDefaultResult)
        => BuildResultOnly(result);
}
