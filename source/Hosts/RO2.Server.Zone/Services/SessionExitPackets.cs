using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Constant-body acknowledgements sent when a player leaves the world, by character switch or logout.
/// </summary>
internal static class SessionExitPackets
{
    /// <summary>RMI 2305 OnCharacterDisconnectNotify message tag (0xD2F5), blob+2.</summary>
    internal const ushort MsgTagDisconnectNotify = 54005;

    /// <summary>2305 reason (blob+8) of an ordinary logout: no reason, the client leaves quietly.</summary>
    internal const ushort DisconnectReasonNone = 0;

    /// <summary>
    /// Builds RMI 2305 OnCharacterDisconnectNotify. 12 bytes on the wire.
    /// </summary>
    /// <remarks>
    /// Layout: <c>[compactLen 01 0A][u16 selfLen = 10][u16 msgTag = 0xD2F5][u32 0 (blob+4)][u16 reason (blob+8)]</c>.
    /// </remarks>
    internal static byte[] BuildDisconnectNotify2305(ushort reason)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDisconnectNotify); // blob+2
            p.WriteUInt32(0);                      // blob+4  never read
            p.WriteUInt16(reason);                 // blob+8  0 for a plain logout
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2305 OnCharacterDisconnectNotify — the logout acknowledgement.
    /// Bytes: <c>01 0A 0A 00 F5 D2 00 00 00 00 00 00</c>.
    /// </summary>
    internal static readonly byte[] DisconnectNotify2305 = BuildDisconnectNotify2305(DisconnectReasonNone);

    /// <summary>
    /// RMI 2938 — sent on both exit paths: a compact-length empty ByteArray (length marker 1, value 0),
    /// bytes <c>01 00</c>.
    /// </summary>
    internal static readonly byte[] Ack2938 = BuildCompactEmpty();

    /// <summary>First constant word of a 2312 body (blob+0), 0x0508. Below the msgTag id space, so payload.</summary>
    private const ushort ChangePcWord0 = 1288;

    /// <summary>Second constant word of a 2312 body (blob+2), 0xC61E. Below the msgTag id space, so payload.</summary>
    /// <remarks>A second variant of this word, 0xFB82, exists; the 0xC61E form is the one sent.</remarks>
    private const ushort ChangePcWord1 = 50718;

    /// <summary>Third constant word of a 2312 body (blob+4), 0x7FF7.</summary>
    private const ushort ChangePcWord2 = 32759;

    /// <summary>Unused word at blob+6 of a 2312 body. Always 0.</summary>
    private const ushort ChangePcReserved = 0;

    /// <summary>The u16 at blob+8 of a 2312 body, the only field the client reads. Always 0.</summary>
    private const ushort ChangePcValue = 0;

    /// <summary>Length of the 2312 blob that follows the compact length: five u16 words.</summary>
    private const int ChangePcBlobLength = 10;

    /// <summary>
    /// Builds RMI 2312 OnChangePC. 12 bytes on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: <c>[compactLen 01 0A][u16 1288][u16 50718][u16 32759][u16 0 (blob+6)][u16 value (blob+8)]</c>.
    /// </para>
    /// <para>
    /// This opcode does NOT follow the usual envelope: blob+0 is 1288 and blob+2 is 50718, both below the
    /// message-tag id space (52001..58524), so those bytes are payload rather than a selfLen/msgTag header. That is
    /// a documented exception, which is why the blob is written as bare payload words after the compact length
    /// instead of through <see cref="PacketWriter.WriteCompactBABody"/>.
    /// </para>
    /// </remarks>
    internal static byte[] BuildChangePc2312()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactLength(ChangePcBlobLength);
        pw.WriteUInt16(ChangePcWord0);    // blob+0  1288
        pw.WriteUInt16(ChangePcWord1);    // blob+2  50718
        pw.WriteUInt16(ChangePcWord2);    // blob+4  32759
        pw.WriteUInt16(ChangePcReserved); // blob+6  unused
        pw.WriteUInt16(ChangePcValue);    // blob+8  the u16 the client compares
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2312 OnChangePC, the character-SWITCH acknowledgement, not sent on logout.
    /// Bytes: <c>01 0A 08 05 1E C6 F7 7F 00 00 00 00</c>.
    /// </summary>
    internal static readonly byte[] ChangePc2312 = BuildChangePc2312();

    private static byte[] BuildCompactEmpty()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactLength(0);
        return pw.ToArray();
    }
}
