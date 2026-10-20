using System.Buffers.Binary;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Party loot dice messages: the two requests (1379 ReqPartyDiceStart, 1380 ReqPartyDiceJoin) and the three
/// notifies (2468 OnLootingBoxRouletteOpen, 2469 OnLootingBoxRouletteResult, 2470 OnLootingBoxRouletteRefuse).
/// <see cref="PartyDiceService"/> decides when each one is sent.
/// </summary>
/// <remarks>
/// <para>
/// All five are compact ByteArrays. Every blob starts with <c>[u16 innerSize == blob length][u16 msgTag]</c>.
/// The notifies are only displayed by the client, which looks up the rolling member by GID.
/// </para>
/// </remarks>
public static class PartyDicePackets
{
 /// <summary>msgTag of 1379.</summary>
    public const ushort MsgTagReqPartyDiceStart = PartyAnswerPackets.MsgTagReqPartyDiceStart;

 /// <summary>msgTag of 1380.</summary>
    public const ushort MsgTagReqPartyDiceJoin = PartyAnswerPackets.MsgTagReqPartyDiceJoin;

 /// <summary>msgTag of 2468.</summary>
    public const ushort MsgTagRouletteOpen = 58256;

 /// <summary>msgTag of 2469.</summary>
    public const ushort MsgTagRouletteResult = 58258;

 /// <summary>msgTag of 2470.</summary>
    public const ushort MsgTagRouletteRefuse = 58259;

 /// <summary>2469 result code when every participant passed. No item is awarded.</summary>
    public const short ResultAllPassed = 15731;

 /// <summary>
 /// Parses 1379 ReqPartyDiceStart: the blob <c>[u16 20][u16 58255][u32 own PC handle][u64 corpse item uid @8][u32 corpse handle @16]</c>.
 /// </summary>
 /// <returns>False when the body is too short or has another envelope.</returns>
    public static bool TryParseDiceStart1379(ReadOnlySpan<byte> body, out ulong itemUid, out uint corpseHandle)
    {
        itemUid = 0;
        corpseHandle = 0;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyDiceStart, 20, out _))
            return false;
        itemUid = BinaryPrimitives.ReadUInt64LittleEndian(blob[8..]);
        corpseHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob[16..]);
        return true;
    }

 /// <summary>
 /// Parses 1380 ReqPartyDiceJoin: the blob <c>[u16 19][u16 58257][u32 own PC handle][u16 @8][u8 choice @10][u64 item uid @11]</c>.
 /// Choice 0 and 1 are rolls in the high and low band, and choice 2 is a pass.
 /// </summary>
 /// <returns>False when the body is too short or has another envelope.</returns>
    public static bool TryParseDiceJoin1380(ReadOnlySpan<byte> body, out byte choice, out ulong itemUid)
    {
        choice = 0;
        itemUid = 0;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyDiceJoin, 19, out _))
            return false;
        choice = blob[10];
        itemUid = BinaryPrimitives.ReadUInt64LittleEndian(blob[11..]);
        return true;
    }

 /// <summary>
 /// Builds 2468 OnLootingBoxRouletteOpen: the blob <c>[u16 21][u16 58256][u32 0][u64 corpse item uid @8][u32 template @16][u8 stack count @20]</c>.
 /// </summary>
    public static byte[] BuildRouletteOpen2468(ulong itemUid, uint itemTemplateId, int count)
    {
        using var pw = new PacketWriter(23);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagRouletteOpen);
            p.WriteUInt32(0);
            p.WriteUInt64(itemUid);
            p.WriteUInt32(itemTemplateId);
            p.WriteByte((byte)Math.Clamp(count, 1, byte.MaxValue));
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2469 OnLootingBoxRouletteResult: the blob <c>[u16 32][u16 58258][u32 0][i16 result @8][u16 roll @10][u64 roller GID @12][u32 template @20][u64 corpse item uid @24]</c>.
 /// A roll carries result 0 with the member's roll and GID. The all-passed notice carries <see cref="ResultAllPassed"/>, roll 0 and GID 0.
 /// </summary>
    public static byte[] BuildRouletteResult2469(short result, ushort roll, ulong rollerGid, uint itemTemplateId, ulong itemUid)
    {
        using var pw = new PacketWriter(34);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagRouletteResult);
            p.WriteUInt32(0);
            p.WriteInt16(result);
            p.WriteUInt16(roll);
            p.WriteUInt64(rollerGid);
            p.WriteUInt32(itemTemplateId);
            p.WriteUInt64(itemUid);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2470 OnLootingBoxRouletteRefuse: the blob <c>[u16 28][u16 58259][u32 0][u64 passer GID @8][u32 template @16][u64 corpse item uid @20]</c>.
 /// It names a member who passed on the item.
 /// </summary>
    public static byte[] BuildRouletteRefuse2470(ulong passerGid, uint itemTemplateId, ulong itemUid)
    {
        using var pw = new PacketWriter(30);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagRouletteRefuse);
            p.WriteUInt32(0);
            p.WriteUInt64(passerGid);
            p.WriteUInt32(itemTemplateId);
            p.WriteUInt64(itemUid);
        });
        return pw.ToArray();
    }
}
