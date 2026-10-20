using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builder for RMI 2438 OnCommonError, the generic system-message notification.
/// </summary>
/// <remarks>
/// <para>
/// Body layout is 13 bytes: <c>[01 0b]</c> followed by an 11-byte blob
/// <c>[u16 selfLen=11][u16 msgTag][u32 0][u16 systemMessageId @blob+8][u8 mode @blob+10]</c>.
/// The u32 at blob+4 is reserved and always 0.
/// </para>
/// <para>
/// The client shows the message identified by <c>systemMessageId</c>. The <c>mode</c> byte selects
/// the trigger context: <see cref="ModeTrade"/> for a failed trade confirmation and
/// <see cref="ModeLoot"/> for an item that auto-loot could not place.
/// </para>
/// <para>
/// The message is unicast to the receiver and is sent on the unencrypted header path.
/// </para>
/// </remarks>
public static class CommonErrorPackets
{
    /// <summary>Message tag of 2438 (58191, 0xE34F), at blob+2.</summary>
    public const ushort MsgTagCommonError = 58191;

    /// <summary>System message 20020 (0x4E34): no free bag cell.</summary>
    public const ushort NoFreeBagCell = 20020;

    /// <summary>Mode value (blob+10) for a refusal after a trade's final confirm.</summary>
    public const byte ModeTrade = 0;

    /// <summary>Mode value (blob+10) for a refusal of an item that kill auto-loot could not place.</summary>
    public const byte ModeLoot = 3;

    /// <summary>Value of the reserved u32 at blob+4.</summary>
    private const uint Reserved = 0;

    /// <summary>
    /// Builds the 13-byte 2438 body: <c>[01 0b][0b 00][4f e3][00 00 00 00][u16 systemMessageId][u8 mode]</c>.
    /// </summary>
    /// <param name="systemMessageId">System message to show (blob+8).</param>
    /// <param name="mode">Trigger context (blob+10), <see cref="ModeTrade"/> or <see cref="ModeLoot"/>.</param>
    public static byte[] BuildCommonError2438(ushort systemMessageId, byte mode)
    {
        using var pw = new PacketWriter(16);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagCommonError); // blob+2
            p.WriteUInt32(Reserved);          // blob+4
            p.WriteUInt16(systemMessageId);   // blob+8
            p.WriteByte(mode);                // blob+10
        });
        return pw.ToArray();
    }
}
