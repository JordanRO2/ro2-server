using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro2.DataPipeline;

/// <summary>
/// Plain-text .tbl inputs. Two ports of the private repo's Python tools:
///   - zone tables (spawn/group/AreaInfo/PointLightInfo.tbl): tools/tbl_to_xlsx.py + the JSON step of
///     tools/convert_xlsx.py, collapsed into one step (no xlsx in between);
///   - string tables (UTF-16LE, "id TAB text"): tools/convert_strings.py.
/// </summary>
internal static partial class TblTables
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    // ---------------------------------------------------------------- zone tables

    /// <summary>Column kinds from the hard-coded type rows in tbl_to_xlsx.py.</summary>
    private enum Kind { Dword, Float, Text }

    private sealed record Column(string Name, Kind Kind);

    // tbl_to_xlsx.py:121-127 (collect_spawn_data), 21 columns including the prepended MapId.
    private static readonly Column[] SpawnColumns =
    [
        new("MapId", Kind.Dword), new("Version", Kind.Dword), new("Index", Kind.Dword),
        new("PosX", Kind.Float), new("PosY", Kind.Float), new("PosZ", Kind.Float), new("Rot", Kind.Float),
        new("AssetIndex", Kind.Dword), new("HoverRadius", Kind.Float), new("Triggered", Kind.Dword),
        new("SpawnRadius", Kind.Float), new("TotalPathPoints", Kind.Dword), new("PP_Index", Kind.Dword),
        new("PP_PosX", Kind.Float), new("PP_PosY", Kind.Float), new("PP_PosZ", Kind.Float),
        new("PP_ActionIndex", Kind.Dword), new("PP_SpeechIndex", Kind.Dword), new("PP_Hour", Kind.Dword),
        new("PP_Minute", Kind.Dword), new("PP_Second", Kind.Dword),
    ];

    // tbl_to_xlsx.py collect_group_data
    private static readonly Column[] GroupColumns =
    [
        new("MapId", Kind.Dword), new("Version", Kind.Dword), new("Index", Kind.Dword),
        new("Name", Kind.Text), new("MaxActive", Kind.Dword), new("RespawnDelay", Kind.Float),
        new("Linked", Kind.Dword), new("RespawnDelayMax", Kind.Float), new("GroupTypeID", Kind.Dword),
        new("UpdateCode", Kind.Text), new("TotalAnchors", Kind.Dword), new("IndexStart", Kind.Dword),
    ];

    // tbl_to_xlsx.py collect_area_info
    private static readonly Column[] AreaColumns =
    [
        new("MapId", Kind.Dword), new("AreaName", Kind.Text), new("Color", Kind.Dword),
        new("CelestialSequence", Kind.Text), new("BGM_Path", Kind.Text), new("Wind", Kind.Float),
    ];

    // tbl_to_xlsx.py collect_pointlight_data
    private static readonly Column[] PointLightColumns =
    [
        new("MapId", Kind.Dword), new("PosX", Kind.Float), new("PosY", Kind.Float), new("PosZ", Kind.Float),
        new("Dimmer", Kind.Float), new("ColorR", Kind.Float), new("ColorG", Kind.Float),
        new("ColorB", Kind.Float), new("AttC", Kind.Float), new("AttL", Kind.Float),
        new("AttQ", Kind.Float), new("Indoor", Kind.Dword),
    ];

    /// <summary>One map folder's .tbl payloads, keyed by lower-case file name.</summary>
    public sealed record MapFiles(string MapFolder, VdkArchive Archive, Dictionary<string, VdkEntry> Files);

    [GeneratedRegex(@"(^|/)BACKGROUND/ZONE/([^/]+)/([^/]+\.tbl)$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneTblPath();

    /// <summary>Map folders inside a ZONE*.VDK, sorted as Python's sorted(os.listdir) does (ordinal).</summary>
    public static List<MapFiles> DiscoverZoneMaps(VdkArchive vdk)
    {
        var maps = new Dictionary<string, Dictionary<string, VdkEntry>>(StringComparer.Ordinal);
        foreach (var e in vdk.Files)
        {
            var m = ZoneTblPath().Match(e.Path);
            if (!m.Success) continue;
            string folder = m.Groups[2].Value;
            if (!maps.TryGetValue(folder, out var files))
                maps[folder] = files = new Dictionary<string, VdkEntry>(StringComparer.OrdinalIgnoreCase);
            files[m.Groups[3].Value] = e;
        }
        return maps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MapFiles(kv.Key, vdk, kv.Value)).ToList();
    }

    public static RecordSet CollectSpawn(IEnumerable<MapFiles> maps) =>
        CollectTable(maps, "spawn.tbl", SpawnColumns);

    /// <summary>
    /// group.tbl rows continue past IndexStart with one anchor index per anchor. The old xlsx path kept
    /// them as unnamed columns col_12, col_13, ... (the server's generated ZoneGroup model has those
    /// properties), padding every row with zeros up to the widest row (~800 columns, 38 MB). Here a row
    /// carries only the extra cells it really has, so the values are identical and the padding is gone.
    /// </summary>
    public static RecordSet CollectGroup(IEnumerable<MapFiles> maps) =>
        CollectTable(maps, "group.tbl", GroupColumns, keepExtraCells: true);

    public static RecordSet CollectPointLight(IEnumerable<MapFiles> maps) =>
        CollectTable(maps, "PointLightInfo.tbl", PointLightColumns);

    private static RecordSet CollectTable(IEnumerable<MapFiles> maps, string fileName, Column[] columns,
        bool keepExtraCells = false)
    {
        var rows = new List<object[]>();
        int widest = columns.Length;
        foreach (var map in maps)
        {
            if (!int.TryParse(map.MapFolder, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
                continue; // tbl_to_xlsx.py: int(map_folder) failure -> skip folder (e.g. "textures")
            if (!map.Files.TryGetValue(fileName, out var entry)) continue;

            var (_, tblRows) = ParseTbl(DecodeText(map.Archive.ReadFile(entry)));
            foreach (var parts in tblRows)
            {
                // tbl_to_xlsx.py: all_rows.append([str(map_id)] + row); extra cells beyond the
                // declared columns are dropped, missing ones become 0 / "".
                int width = keepExtraCells ? Math.Max(columns.Length, parts.Length + 1) : columns.Length;
                var row = new object[width];
                for (int c = 0; c < width; c++)
                {
                    string? raw = c == 0 ? mapId.ToString(CultureInfo.InvariantCulture)
                        : c - 1 < parts.Length ? parts[c - 1] : null;
                    row[c] = ConvertCell(raw, c < columns.Length ? columns[c].Kind : Kind.Dword);
                }
                if (RecordSet.IsZeroOrEmpty(row[0])) continue;
                rows.Add(row);
                widest = Math.Max(widest, width);
            }
        }

        var keys = columns.Select(c => c.Name).ToList();
        for (int c = columns.Length; c < widest; c++)
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"col_{c}"));
        return new RecordSet(keys.ToArray(), rows);
    }

    /// <summary>tbl_to_xlsx.py collect_area_info: "key TAB value" lines, one record per map.</summary>
    public static RecordSet CollectAreaInfo(IEnumerable<MapFiles> maps)
    {
        var rows = new List<object[]>();
        foreach (var map in maps)
        {
            if (!int.TryParse(map.MapFolder, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
                continue;
            if (!map.Files.TryGetValue("AreaInfo.tbl", out var entry)) continue;

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MapId"] = mapId.ToString(CultureInfo.InvariantCulture),
            };
            foreach (string rawLine in SplitLines(DecodeText(map.Archive.ReadFile(entry))))
            {
                string line = rawLine.Trim(); // python strip(): also drops a trailing TAB, so "key TAB" has no value
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                data[line[..tab]] = line[(tab + 1)..];
            }

            string Get(string key, string fallback) => data.TryGetValue(key, out var v) ? v : fallback;
            object[] row =
            [
                ConvertCell(Get("MapId", "0"), Kind.Dword),
                ConvertCell(Get("areaName", ""), Kind.Text),
                ConvertCell(Get("color", "0"), Kind.Dword),
                ConvertCell(Get("CelestialSequence", ""), Kind.Text),
                ConvertCell(Get("BGM Path", ""), Kind.Text),
                ConvertCell(Get("Wind", "0"), Kind.Float),
            ];
            if (!RecordSet.IsZeroOrEmpty(row[0])) rows.Add(row);
        }
        return new RecordSet(AreaColumns.Select(c => c.Name).ToArray(), rows);
    }

    /// <summary>
    /// Mirrors write_xlsx (int(float(v)) / float(v), raw text on failure) followed by convert_value
    /// (int()/float()/str(), 0 on failure; None -> 0 for numerics, "" for text).
    /// </summary>
    private static object ConvertCell(string? raw, Kind kind)
    {
        if (raw is null) return kind == Kind.Text ? "" : 0L;
        switch (kind)
        {
            case Kind.Text:
                return raw;
            case Kind.Dword:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && double.IsFinite(d) ? (long)Math.Truncate(d) : 0L;
            default:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                    && double.IsFinite(f) ? f : 0.0;
        }
    }

    /// <summary>
    /// tbl_to_xlsx.py:parse_tbl. Non-blank lines; the header is the first TAB-containing line not
    /// starting with "Total"/"ver" whose cells are mostly alphabetic; data rows have >= 2 cells.
    /// </summary>
    private static (string[] Headers, List<string[]> Rows) ParseTbl(string text)
    {
        var lines = SplitLines(text).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return ([], []);

        int headerIdx = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Contains('\t') && !line.StartsWith("Total", StringComparison.Ordinal)
                && !line.StartsWith("ver", StringComparison.Ordinal))
            {
                string[] parts = line.Split('\t');
                int alpha = parts.Count(p => p.Length > 0 && char.IsLetter(p[0]));
                if (alpha > parts.Length / 2.0) { headerIdx = i; break; }
            }
        }

        var rows = new List<string[]>();
        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            string[] parts = lines[i].Split('\t');
            if (parts.Length >= 2) rows.Add(parts);
        }
        return (lines[headerIdx].Split('\t'), rows);
    }

    /// <summary>Python universal newlines, with trailing CR/LF removed from each line.</summary>
    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    /// <summary>
    /// Zone .tbl files are UTF-8 when valid, otherwise Korean ANSI (CP949). The Python tool decoded
    /// everything as UTF-8 with replacement, which turned Korean names into U+FFFD runs; this is an
    /// intentional difference (only Name/UpdateCode text in ZoneGroup is affected).
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encodings.Cp949.GetString(bytes); }
    }

    // ---------------------------------------------------------------- string tables

    /// <summary>
    /// convert_strings.py:parse_string_tbl. UTF-16 (BOM FF FE / FE FF) or UTF-8 text; each line is
    /// "numeric id TAB text"; blank text and non-numeric ids (header rows) are skipped; a repeated id
    /// keeps its first position and takes the last text (Python dict).
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseStringTbl(byte[] raw)
    {
        string text;
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        else
            text = Encoding.UTF8.GetString(raw);

        var entries = new List<KeyValuePair<string, string>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim('\r', '\n', '\t', ' ');
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            if (tab < 0) continue;

            string id = line[..tab].Trim();
            string value = line[(tab + 1)..].Trim();
            if (id.Length == 0 || !id.All(char.IsAsciiDigit)) continue;
            if (value.Length == 0) continue;

            if (index.TryGetValue(id, out int at))
            {
                entries[at] = new KeyValuePair<string, string>(id, value);
            }
            else
            {
                index[id] = entries.Count;
                entries.Add(new KeyValuePair<string, string>(id, value));
            }
        }
        return entries;
    }
}
