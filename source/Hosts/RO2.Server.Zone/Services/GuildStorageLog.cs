using System.Globalization;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Zone-side half of the guild vault history: line kinds, page size, the Config_Def readers and the 2678 and
/// 2679 packet builders. Persistence is provided by the guild storage log interface, which stores the item
/// history (paged by 1491) and the funds history (paged by 1492).
/// </summary>
public static class GuildStorageLog
{
    public const byte KindDeposit = 0;
    public const byte KindWithdraw = 1;

 /// <summary>Lines per history answer.</summary>
    public const int PageSize = 13;

 /// <summary>Body length of one item history line in 2678.</summary>
    private const int ItemLineLen = 64;

    /// <summary>Size of the actor name field at line offset +16: UTF-16LE, NUL-terminated.</summary>
    private const int NameFieldLen = 36;

    /// <summary>Longest actor name in UTF-16 code units; one unit of the field is kept for the NUL terminator.</summary>
    private const int MaxNameChars = NameFieldLen / 2 - 1;

    /// <summary>Result code of an accepted 2678 / 2679 answer.</summary>
    private const ushort ResultOk = 0;

    /// <summary>Item history line offset +52 as written on 2678 lines (all bits set).</summary>
    private const uint ItemLineMarker = 0xFFFFFFFF;

    /// <summary>Funds history line offset +67 (referenced id), only read by the client for kinds 2 and 3.</summary>
    private const uint NoReferencedId = 0;

 /// <summary>Body length of one funds history line in 2679.</summary>
    private const int FundsLineLen = 71;

 /// <summary>Config_Def type of the guild storage history lines per page.</summary>
    internal const ushort ConfigHistoryPageSize = 14;

 /// <summary>
 /// Config_Def type of the guild storage history retention in days. The center server deletes older history at
 /// startup.
 /// </summary>
    internal const ushort ConfigHistoryRetentionDays = 15;

 /// <summary>
 /// Key under which a guild's history is stored: the guild's 16-byte GUID. The numeric guild id is not used,
 /// because it can be reused after a guild is disbanded.
 /// </summary>
    internal static byte[] Guid16Of(Guild g) => g.Guid16 ?? Array.Empty<byte>();

 /// <summary>
 /// Lines per funds history page, from Config_Def type 14. The client pages the answer by dividing its total
 /// by 13, which is also the fallback when the row is missing.
 /// </summary>
    internal static int HistoryPageSize(IReadOnlyList<ConfigDef> config)
        => ConfigInt(config, ConfigHistoryPageSize) is int n and > 0 ? n : PageSize;

 /// <summary>
 /// History retention in days from Config_Def type 15, or 0 (no bound) when the row is missing.
 /// </summary>
    internal static int HistoryRetentionDays(IReadOnlyList<ConfigDef> config)
        => ConfigInt(config, ConfigHistoryRetentionDays) is int n and > 0 ? n : 0;

    private static int? ConfigInt(IReadOnlyList<ConfigDef> config, ushort type)
    {
        var row = config.FirstOrDefault(c => c.type == type);
        return row != null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

 /// <summary>
 /// Builds the 2678 AnsGuildStorageStatement_Item body: <c>[u16 result][u32 len][u32 total][u16 n][n x 64-byte line]</c>,
 /// where len counts the bytes after itself.
 /// </summary>
 /// <remarks>
 /// <code>
 /// +0 f64 OLE date, u32 0, u32 = high dword of the double
 /// +16 actor name, UTF-16LE, NUL-terminated, 36 bytes
 /// +52 u32
 /// +56 u16 0
 /// +58 u8 kind: 0 deposit, 1 withdraw
 /// +59 u32 item template id
 /// +63 u8 count
 /// </code>
 /// <para>
 /// The bytes after the name's NUL terminator are zeroed.
 /// </para>
 /// </remarks>
    public static byte[] BuildAnsStatementItem2678(long total, IReadOnlyList<GuildStorageLogEntry> lines)
    {
        using var w = new PacketWriter(12 + lines.Count * ItemLineLen);
        w.WriteUInt16(ResultOk);                                      // result
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteUInt32((uint)Math.Clamp(total, 0, uint.MaxValue));     // lines in the window
        w.WriteUInt16((ushort)lines.Count);                           // lines in this answer
        foreach (var e in lines)
        {
            WriteDate(w, e.WhenUtc);                                  // +0
            WriteName(w, e.ActorName);                                // +16
            w.WriteUInt32(ItemLineMarker);                            // +52
            w.WriteUInt16(0);                                         // +56
            w.WriteByte(e.Kind);                                      // +58
            w.WriteUInt32(e.ItemId);                                  // +59
            w.WriteByte((byte)Math.Clamp(e.Count, 0, byte.MaxValue)); // +63
        }
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2679 AnsGuildStorageStatement_Zeny body: <c>[u16 result 0][u32 len][u32 total][u16 n][n x 71-byte line]</c>,
 /// where len counts the bytes after itself. The total is the number of lines in the requested day window, not in
 /// this page. With no lines the body is <c>[0][6][0][0]</c>.
 /// </summary>
 /// <remarks>
 /// <para>
 /// One line:
 /// </para>
 /// <code>
 /// +0 f64 OLE date, u32 0, u32 = high dword of the double
 /// +16 actor name, UTF-16LE, NUL-terminated, 36 bytes
 /// +52 u32 0
 /// +56 u16 0
 /// +58 u8 kind: 0 deposit, 1 withdraw, 2 use, 3 defeat (text template selected by the client)
 /// +59 u64 amount, as two dwords
 /// +67 u32 referenced id
 /// </code>
 /// <para>
 /// This server logs only deposit and withdraw lines, so kinds 2 and 3 are never written.
 /// </para>
 /// </remarks>
    public static byte[] BuildAnsStatementZeny2679(long total, IReadOnlyList<GuildFundsLogEntry> lines)
    {
        using var w = new PacketWriter(12 + lines.Count * FundsLineLen);
        w.WriteUInt16(ResultOk);                                      // result
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteUInt32((uint)Math.Clamp(total, 0, uint.MaxValue));     // lines in the window
        w.WriteUInt16((ushort)lines.Count);                           // lines in this answer
        foreach (var e in lines)
        {
            WriteDate(w, e.WhenUtc);                                  // +0
            WriteName(w, e.ActorName);                                // +16
            w.WriteUInt32(0);                                         // +52
            w.WriteUInt16(0);                                         // +56
            w.WriteByte(e.Kind);                                      // +58
            w.WriteUInt64((ulong)Math.Max(0, e.Amount));              // +59
            w.WriteUInt32(NoReferencedId);                            // +67
        }
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>Writes a history line date from the server clock, in the 16-byte form.</summary>
    private static void WriteDate(PacketWriter w, DateTime whenUtc)
    {
        double oa;
        try { oa = DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc).ToLocalTime().ToOADate(); }
        catch { oa = 0; }
        w.WriteDouble(oa);
        w.WriteUInt32(0);
        w.WriteUInt32((uint)(BitConverter.DoubleToInt64Bits(oa) >> 32));
    }

    private static void WriteName(PacketWriter w, string? name)
    {
        var field = new byte[NameFieldLen];
        name ??= string.Empty;
        var n = Math.Min(name.Length, MaxNameChars);
        for (int i = 0; i < n; i++)
        {
            field[i * 2] = (byte)name[i];
            field[i * 2 + 1] = (byte)(name[i] >> 8);
        }
        w.WriteBytes(field);
    }
}
