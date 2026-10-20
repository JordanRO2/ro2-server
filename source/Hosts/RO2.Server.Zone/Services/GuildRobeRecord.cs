using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The guild robe record carried in guild lines: 2559 AnsMyGuildInfo, the PC record's guild block, 2591
/// NfyPCViewInfoUpdate, 2561 AnsGuildCreate and the 2929 robe list. Holds the same four values in two layouts.
/// </summary>
/// <remarks>
/// <para>
/// This server has no robe purchase or registration, so every guild wears the Basic Robe: the item
/// 12711460 ("Basic robe", Item_Type 52), colour 0xFF9FAF81, date status 0 and an "unregistered" date: the Unix
/// epoch as an OLE date. The pad word is 0.
/// </para>
/// <code>
/// info form, 25 B (2559, PC record, 2929, 2927 push): [u32 robe item id][f64 OLE date][u32 date status][u32 pad][u32 colour][u8 0x80]
/// view form, 24 B (2591, 2561): [u32 robe item id][u32 colour][f64 OLE date][u32 date status][u32 pad]
/// </code>
/// <para>
/// Both forms carry the same four values: the u32 date status (0, valid) and the u32 pad (0) are never written, they
/// are left as cleared. The info form's final byte is the constant 0x80.
/// </para>
/// <para>
/// The selection record used by 2927 answers is a different layout, see <c>GuildRobeChangedPackets</c>.
/// </para>
/// <para>
/// The Basic Robe item is not one of the five buyable robes (12711461-12711465) of the guild robe data; it is the robe
/// of a guild that never registered one.
/// </para>
/// <para>
/// The date is computed rather than hard-coded: the Unix epoch converted to server local time, through the
/// same conversion as every other OLE date this server sends (<c>GuildRosterPackets.WriteOleDate</c>).
/// </para>
/// </remarks>
public static class GuildRobeRecord
{
    /// <summary>Item id of the Basic Robe, the robe of a guild that has not registered one.</summary>
    public const uint BasicRobeItemId = 12711460;

    /// <summary>Colour of the Basic Robe in guild records.</summary>
    public const uint BasicRobeColor = 0xFF9FAF81;

    /// <summary>The info form's last byte: the constant 0x80 in every record.</summary>
    internal const byte InfoFormTail = 0x80;

    /// <summary>Size of the info form in bytes.</summary>
    internal const int InfoFormLength = 25;

    /// <summary>Size of the view form in bytes.</summary>
    internal const int ViewFormLength = 24;

    /// <summary>Offset of the robe item id (u32) in both forms.</summary>
    private const int ItemIdOffset = 0;

    /// <summary>Info form: offset of the OLE date (f64). The u32 date status follows at +12 and the u32 pad at +16, both 0.</summary>
    private const int InfoDateOffset = 4;

    /// <summary>Info form: offset of the colour (u32).</summary>
    private const int InfoColorOffset = 20;

    /// <summary>Info form: offset of the trailing constant byte.</summary>
    private const int InfoTailOffset = 24;

    /// <summary>View form: offset of the colour (u32).</summary>
    private const int ViewColorOffset = 4;

    /// <summary>View form: offset of the OLE date (f64). The u32 date status follows at +16 and the u32 pad at +20, both 0.</summary>
    private const int ViewDateOffset = 8;

    /// <summary>The Basic Robe's date: the Unix epoch as an OLE date in server local time.</summary>
    public static double BasicRobeDate => DateTime.UnixEpoch.ToLocalTime().ToOADate();

    /// <summary>Writes the 25-byte info form of the Basic Robe.</summary>
    public static void WriteInfoForm(Span<byte> dst) => WriteInfoForm(dst, BasicRobeDate);

    /// <summary>Writes the 24-byte view form of the Basic Robe.</summary>
    public static void WriteViewForm(Span<byte> dst) => WriteViewForm(dst, BasicRobeDate);

    /// <summary>
    /// Writes the 25-byte info form with an explicit OLE date, so the bytes do not depend on the machine's time zone.
    /// </summary>
    internal static void WriteInfoForm(Span<byte> dst, double oleDate)
    {
        dst[..InfoFormLength].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ItemIdOffset..], BasicRobeItemId);
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(InfoDateOffset, sizeof(double)), oleDate);
        // +12 u32 date status 0 (valid) and +16 u32 pad 0: left as cleared.
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(InfoColorOffset, sizeof(uint)), BasicRobeColor);
        dst[InfoTailOffset] = InfoFormTail;
    }

    /// <summary>Writes the 24-byte view form with an explicit OLE date.</summary>
    internal static void WriteViewForm(Span<byte> dst, double oleDate)
    {
        dst[..ViewFormLength].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ItemIdOffset..], BasicRobeItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ViewColorOffset, sizeof(uint)), BasicRobeColor);
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(ViewDateOffset, sizeof(double)), oleDate);
        // +16 u32 date status 0 and +20 u32 pad 0: left as cleared.
    }

    /// <summary>Returns the info form as a new array.</summary>
    public static byte[] InfoForm()
    {
        var b = new byte[InfoFormLength];
        WriteInfoForm(b);
        return b;
    }

    /// <summary>Returns the view form as a new array.</summary>
    public static byte[] ViewForm()
    {
        var b = new byte[ViewFormLength];
        WriteViewForm(b);
        return b;
    }
}
