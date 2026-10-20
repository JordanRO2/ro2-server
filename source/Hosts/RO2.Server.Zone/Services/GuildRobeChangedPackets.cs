using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the guild robe selection answers: 2928 AnsGuildSelectRobe and 2927 NfyGuildRobeChanged,
/// sent in reply to 1633 ReqGuildSelectRobe.
/// </summary>
/// <remarks>
/// <para>
/// 2928 carries a 2-byte result code. 2927 carries a 25-byte robe info record, either the selection record
/// or the guild-line record, wrapped in a U32-length envelope.
/// </para>
/// <para>
/// Two kinds of 2927 are sent: the selection answer to a 1633 request, which carries the selected robe, and
/// an unsolicited push of the guild's current robe, which this server does not send.
/// </para>
/// <para>
/// 2927 layout, 30 bytes: <c>[u32 26 (inner length)][25-byte info record][u8 extra, 0]</c>. The info record is
/// <c>[u32 robe item id][f64 OLE date][u32 date status][u32 pad][u32 colour][u8 flags]</c>.
/// </para>
/// </remarks>
internal static class GuildRobeChangedPackets
{
    /// <summary>2928 result code for an accepted selection.</summary>
    internal const ushort SelectAccepted = 0;

    /// <summary>
    /// 2928 result code for a requester who is not the guild master (0x13, 19). The client ignores the answer
    /// for this code, as it does for codes 6 and 7.
    /// </summary>
    internal const ushort SelectNotMaster = 0x13;

    /// <summary>Info record offset of the robe item id (u32). Left 0 in the Basic Robe selection record.</summary>
    private const int ItemIdOffset = 0;

    /// <summary>Info record offset of the OLE date (f64).</summary>
    private const int DateOffset = 4;

    /// <summary>Info record offset of the date status (u32). Left 0 (valid).</summary>
    private const int DateStatusOffset = 12;

    /// <summary>
    /// Info record offset of the pad (u32). In a selection record it holds the high dword of the date's bit
    /// pattern.
    /// </summary>
    private const int PadOffset = 16;

    /// <summary>Info record offset of the colour (u32).</summary>
    private const int ColorOffset = 20;

    /// <summary>Info record offset of the trailing flags byte.</summary>
    private const int FlagsOffset = 24;

    /// <summary>
    /// Date of the Basic Robe selection record: 2000-11-11 11:11:11 as an OLE date. It is a fixed sentinel value
    /// and is built from the calendar date directly, without a local-time conversion.
    /// </summary>
    internal static readonly double BasicRobeSelectedDate = new DateTime(2000, 11, 11, 11, 11, 11).ToOADate();

    /// <summary>
    /// Builds the 25-byte info-form record sent when the master selects the Basic Robe.
    /// </summary>
    /// <remarks>
    /// The pad field holds the high dword of the date's bit pattern, and the item id is 0. The record is
    /// transient: the next guild-line record restores the current robe.
    /// </remarks>
    internal static byte[] BasicRobeSelectedRecord()
    {
        var b = new byte[GuildRobeRecord.InfoFormLength];
        // ItemIdOffset (item id 0) and DateStatusOffset (status 0): left as allocated.
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(DateOffset, sizeof(double)), BasicRobeSelectedDate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PadOffset, sizeof(uint)), (uint)(BitConverter.DoubleToUInt64Bits(BasicRobeSelectedDate) >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ColorOffset, sizeof(uint)), GuildRobeRecord.BasicRobeColor);
        b[FlagsOffset] = GuildRobeRecord.InfoFormTail;
        return b;
    }

    /// <summary>Size of the 2927 envelope's u32 inner-length prefix.</summary>
    private const int LengthPrefixLength = sizeof(uint);

    /// <summary>Size of the extra byte after the info record in 2927. Always 0.</summary>
    private const int ExtraByteLength = 1;

    /// <summary>2927 body length: u32 inner length plus 26 bytes (the 25-byte record and the extra byte).</summary>
    internal const int RobeChangedBytes = LengthPrefixLength + GuildRobeRecord.InfoFormLength + ExtraByteLength;

    /// <summary>Builds the 2928 AnsGuildSelectRobe body: <c>[u16 code]</c>.</summary>
    internal static byte[] BuildAnsGuildSelectRobe2928(ushort code)
    {
        var b = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, code);
        return b;
    }

    /// <summary>
    /// Builds a 2927 NfyGuildRobeChanged body carrying <paramref name="infoForm"/>, a 25-byte robe info record.
    /// </summary>
    internal static byte[] BuildNfyGuildRobeChanged2927(ReadOnlySpan<byte> infoForm)
    {
        if (infoForm.Length != GuildRobeRecord.InfoFormLength)
            throw new ArgumentException($"A robe record's info form is {GuildRobeRecord.InfoFormLength} bytes, got {infoForm.Length}.");
        var b = new byte[RobeChangedBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(RobeChangedBytes - LengthPrefixLength));   // inner length: 26
        infoForm.CopyTo(b.AsSpan(LengthPrefixLength));
        // The trailing extra byte is 0: left as allocated.
        return b;
    }
}
