using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builder for RMI 2534 OnNotifyCanGatherDeadNpc, which marks a corpse as gatherable on the receiving client.
/// </summary>
/// <remarks>
/// <para>
/// The body is 15 bytes: <c>[01 0d]</c> followed by a 13-byte blob
/// <c>[u16 selfLen=13][u16 msgTag][u32 0][u32 corpse NPC handle @blob+8][u8 flag @blob+12]</c>.
/// The flag is always 1 (gatherable).
/// </para>
/// <para>
/// The notice is sent once the corpse has no loot left for the viewer, so a client can gather the corpse only
/// after loot has been emptied.
/// </para>
/// </remarks>
public static class CorpseGatherPackets
{
 /// <summary>Message tag of 2534 (58363, 0xE3FB).</summary>
    public const ushort MsgTagNotifyCanGatherDeadNpc = 58363;

 /// <summary>Length of the 2534 body in bytes: the compact length prefix, the 16-bit self length and the 13-byte blob.</summary>
    public const int BodyBytes = 15;

 /// <summary>Value of the reserved 32-bit field at blob offset +4. It is always zero.</summary>
    public const uint ReservedField = 0;

 /// <summary>Value of the gatherable flag at blob offset +12. Only 1 (gatherable) is ever sent.</summary>
    public const byte GatherableFlag = 1;

 /// <summary>
 /// Builds the 15-byte 2534 body for <paramref name="corpseHandle"/>, flag 1 (gatherable).
 /// </summary>
 /// <param name="corpseHandle">The dead NPC's actor handle.</param>
    public static byte[] BuildNotifyCanGatherDeadNpc2534(uint corpseHandle)
    {
        using var pw = new PacketWriter(BodyBytes + 1);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNotifyCanGatherDeadNpc); // blob+2
            p.WriteUInt32(ReservedField);                // blob+4
            p.WriteUInt32(corpseHandle);                 // blob+8
            p.WriteByte(GatherableFlag);                 // blob+12
        });
        return pw.ToArray();
    }
}
