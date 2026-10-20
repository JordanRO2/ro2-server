using System.Text;
using System.Text.RegularExpressions;

namespace Ro2.DataPipeline;

/// <summary>
/// Regenerates the client-derived data/*.json tables from a user's own Ragnarok Online 2 client:
///   Data/ASSET.VDK            -> *.ct tables            -> &lt;outDir&gt;/&lt;name&gt;.json
///   Data/ZONE.VDK + ZONE1.VDK -> spawn/group/AreaInfo/PointLightInfo.tbl -> ZoneSpawn/ZoneGroup/ZoneAreaInfo/ZonePointLight.json
///   Data/1/STRING.VDK         -> LANG/1/string/*.tbl    -> &lt;outDir&gt;/strings/*.json
/// </summary>
internal static partial class Program
{
    private const string Usage = """
        Usage: data-pipeline <clientDir> <outDir> [--only Name,Name,...] [--include-protected] [--bools-as-json]

          clientDir            your "Ragnarok Online 2" client folder (contains Data/)
          outDir               the server's data/ folder
          --only               convert only these outputs (e.g. ItemInfo,ZoneSpawn,strings/string_item_name)
          --include-protected  also overwrite the server-authored tables that share a name with a client table
          --bools-as-json      write BOOL columns as true/false (default 1/0, which is what the server models read)
        """;

    /// <summary>
    /// Server-authored tables tracked in the repo (the data/ whitelist in .gitignore). Several share a
    /// name with a client CT (classinfo, city, Teleport, ...). They are never overwritten unless
    /// --include-protected is passed. Keep in sync with the whitelist in .gitignore.
    /// </summary>
    private static readonly HashSet<string> ServerAuthored = new(StringComparer.OrdinalIgnoreCase)
    {
        "CardMaterial", "Craft_ProJobType", "Custom_Weave_Color_Info", "Custom_Weave_info", "Custom_ZoomInfo",
        "DropTable", "GroupData", "MessengerInfo", "ModelChange_Table", "Monster", "MultiType",
        "NavCellFromMesh", "NpcNavCell", "NpcServerInfo", "Quest_Help_Info", "SetTrait", "SpawnData",
        "Standard_Stat", "Teleport", "TraitInfo", "Weave_Color_Info", "Weave_Info",
        "Weave_Item_Matching_Info", "Weave_Pattern_Info", "Weave_Setting_Unit_Table", "Zone_DB",
        "chat_filter", "city", "classinfo", "custom_Skin", "effectextra", "name_filter", "race_desc",
        // Client tables we server-tuned (refine/enforce economy): ship the tuned copy, do not
        // overwrite it with the retail CT values unless --include-protected is passed.
        "REFINE_RATE", "EnforceResult", "REFINE_INFO", "ItemEnforceInfo",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string _outDir = "";
    private static HashSet<string>? _only;
    private static bool _includeProtected;
    private static int _written, _skipped, _failed, _warnings;
    private static JsonRenderer Json = new();

    private static int Main(string[] args)
    {
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                case "--include-protected":
                    _includeProtected = true;
                    break;
                case "--bools-as-json":
                    Json = new JsonRenderer { BoolsAsJson = true };
                    break;
                case "--only" when i + 1 < args.Length:
                    _only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }
        if (positional.Count != 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string clientDir = Path.GetFullPath(positional[0]);
        _outDir = Path.GetFullPath(positional[1]);
        string dataDir = Path.Combine(clientDir, "Data");
        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine($"error: '{dataDir}' not found - clientDir must be the 'Ragnarok Online 2' folder that contains Data/");
            return 2;
        }
        Directory.CreateDirectory(_outDir);

        Console.WriteLine($"client: {clientDir}");
        Console.WriteLine($"output: {_outDir}");

        RunStage("CT tables", () => ConvertCtTables(Path.Combine(dataDir, "ASSET.VDK")));
        RunStage("Zone tables", () => ConvertZoneTables(dataDir));
        RunStage("String tables", () => ConvertStringTables(Path.Combine(dataDir, "1", "STRING.VDK")));

        Console.WriteLine();
        Console.WriteLine($"Done: {_written} written, {_skipped} skipped, {_failed} failed, {_warnings} warnings");
        if (Json.NonFiniteWritten > 0)
            Console.WriteLine($"warning: {Json.NonFiniteWritten} NaN/Infinity float(s) were written as 0");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunStage(string title, Action stage)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title}");
        try { stage(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _failed++;
            Console.Error.WriteLine($"  FAILED: {ex.Message}");
        }
    }

    private static bool Wanted(string name) => _only is null || _only.Contains(name);

    // ---------------------------------------------------------------- CT stage

    private static void ConvertCtTables(string assetVdk)
    {
        if (!File.Exists(assetVdk))
        {
            Console.Error.WriteLine($"  FAILED: {assetVdk} not found");
            _failed++;
            return;
        }

        using var vdk = VdkArchive.Open(assetVdk);
        CheckCounts(vdk);
        var cts = vdk.Files.Where(e => e.Name.EndsWith(".ct", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"  {Path.GetFileName(assetVdk)}: {vdk.Version}, {vdk.FileCount} files, {cts.Count} .ct tables");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cts)
        {
            string name = Path.GetFileNameWithoutExtension(entry.Name);
            if (!Wanted(name)) continue;
            if (!seen.Add(name))
            {
                Warn($"{entry.Path}: duplicate table name '{name}', ignored");
                continue;
            }
            if (IsProtected(name)) continue;

            try
            {
                var ct = CtTable.Parse(vdk.ReadFile(entry));
                if (ct.UnknownTypes.Count > 0)
                    Warn($"{name}: unknown column type code(s) {string.Join(",", ct.UnknownTypes)} read as INT");
                if (ct.CrcOk == false)
                    Warn($"{name}: CRC16 mismatch (data read anyway)");

                var records = RecordSet.FromCt(ct);
                WriteJson(Path.Combine(_outDir, name + ".json"), Json.RenderTable(records),
                    $"{entry.Name} -> {name}.json", records.Rows.Count);
            }
            catch (InvalidDataException ex)
            {
                _failed++;
                Console.Error.WriteLine($"  FAILED {entry.Name}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- zone stage

    private static void ConvertZoneTables(string dataDir)
    {
        var archives = new List<VdkArchive>();
        try
        {
            var maps = new List<TblTables.MapFiles>();
            foreach (string vdkName in new[] { "ZONE.VDK", "ZONE1.VDK" }) // same order as tbl_to_xlsx.py ZONE_DIRS
            {
                string path = Path.Combine(dataDir, vdkName);
                if (!File.Exists(path))
                {
                    Warn($"{vdkName} not found, its maps are skipped");
                    continue;
                }
                var vdk = VdkArchive.Open(path);
                archives.Add(vdk);
                CheckCounts(vdk);
                var found = TblTables.DiscoverZoneMaps(vdk);
                Console.WriteLine($"  {vdkName}: {vdk.Version}, {found.Count} map folders with .tbl files");
                maps.AddRange(found);
            }
            if (maps.Count == 0) return;

            var outputs = new (string Name, Func<IEnumerable<TblTables.MapFiles>, RecordSet> Collect)[]
            {
                ("ZoneSpawn", TblTables.CollectSpawn),
                ("ZoneGroup", TblTables.CollectGroup),
                ("ZoneAreaInfo", TblTables.CollectAreaInfo),
                ("ZonePointLight", TblTables.CollectPointLight),
            };
            foreach (var (name, collect) in outputs)
            {
                if (!Wanted(name) || IsProtected(name)) continue;
                var records = collect(maps);
                if (records.Rows.Count == 0)
                {
                    // tbl_to_xlsx.py: "SKIP: no data" - no file is written for an empty table.
                    Console.WriteLine($"  SKIP {name}.json: no rows");
                    _skipped++;
                    continue;
                }
                WriteJson(Path.Combine(_outDir, name + ".json"), Json.RenderTable(records),
                    $"{name} (.tbl) -> {name}.json", records.Rows.Count);
            }
        }
        finally
        {
            foreach (var a in archives) a.Dispose();
        }
    }

    // ---------------------------------------------------------------- string stage

    [GeneratedRegex(@"(^|/)LANG/1/(string/[^/]+\.tbl|chat_cmd\.tbl)$", RegexOptions.IgnoreCase)]
    private static partial Regex StringTblPath();

    private static void ConvertStringTables(string stringVdk)
    {
        if (!File.Exists(stringVdk))
        {
            Warn($"{stringVdk} not found, string tables skipped");
            return;
        }

        using var vdk = VdkArchive.Open(stringVdk);
        CheckCounts(vdk);
        var tables = vdk.Files.Where(e => StringTblPath().IsMatch(e.Path))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {Path.GetFileName(stringVdk)}: {vdk.Version}, {tables.Count} string tables");

        string stringsDir = Path.Combine(_outDir, "strings");
        foreach (var entry in tables)
        {
            string stem = Path.GetFileNameWithoutExtension(entry.Name);
            if (!Wanted("strings/" + stem)) continue;

            var parsed = TblTables.ParseStringTbl(vdk.ReadFile(entry));
            if (parsed.Count == 0)
            {
                Console.WriteLine($"  SKIP {entry.Name}: no entries");
                _skipped++;
                continue;
            }
            Directory.CreateDirectory(stringsDir);
            WriteJson(Path.Combine(stringsDir, stem + ".json"), JsonRenderer.RenderStringMap(parsed),
                $"{entry.Name} -> strings/{stem}.json", parsed.Count);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsProtected(string name)
    {
        if (_includeProtected || !ServerAuthored.Contains(name)) return false;
        Console.WriteLine($"  SKIP {name}.json: server-authored table (use --include-protected to overwrite)");
        _skipped++;
        return true;
    }

    private static void WriteJson(string path, StringBuilder text, string label, int count)
    {
        using (var writer = new StreamWriter(path, append: false, Utf8NoBom))
        {
            foreach (var chunk in text.GetChunks())
                writer.Write(chunk.Span);
        }
        _written++;
        Console.WriteLine($"  {label}: {count} records ({new FileInfo(path).Length / 1024} KB)");
    }

    /// <summary>The header's file count must equal the entries found; a mismatch means a damaged or truncated archive.</summary>
    private static void CheckCounts(VdkArchive vdk)
    {
        int found = vdk.Files.Count();
        if (found != vdk.FileCount)
            Warn($"{Path.GetFileName(vdk.FilePath)}: header says {vdk.FileCount} files, found {found} (damaged archive?)");
    }

    private static void Warn(string message)
    {
        _warnings++;
        Console.Error.WriteLine($"  warning: {message}");
    }
}
