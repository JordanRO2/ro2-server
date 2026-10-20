using System.Text.Json;
using System.Text.Json.Serialization;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Max HP and max SP per (asset, level) for NPCs, as shipped in <see cref="ShippedPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// The NPC record sent by the NPC spawn and view messages carries current and max HP and SP. The shipped table
/// supplies the max values by asset and level. Assets that are not in the table fall back to the server's
/// computed vitals (see <c>NpcStateManager.ResolveVitals</c>).
/// </para>
/// </remarks>
public sealed class NpcRetailVitals
{
 /// <summary>Path of the shipped table, relative to the working directory like the other data tables.</summary>
    public static readonly string ShippedPath = Path.Combine("data", "npc", "NpcRetailVitals.json");

    private static readonly Lazy<NpcRetailVitals?> s_shipped = new(() => TryLoad(ShippedPath));

 /// <summary>The shipped table, or null when the file is missing or unreadable.</summary>
    public static NpcRetailVitals? Shipped => s_shipped.Value;

    private readonly Dictionary<(uint Asset, int Level), (int MaxHp, int MaxSp)> _byAssetLevel;
    private readonly Dictionary<int, int> _maxSpByLevel;

    private NpcRetailVitals(Dictionary<(uint, int), (int, int)> byAssetLevel, Dictionary<int, int> maxSpByLevel)
    {
        _byAssetLevel = byAssetLevel;
        _maxSpByLevel = maxSpByLevel;
    }

 /// <summary>Number of (asset, level) entries.</summary>
    public int Count => _byAssetLevel.Count;

 /// <summary>Loads a table from <paramref name="path"/>, or returns null on any failure.</summary>
    public static NpcRetailVitals? TryLoad(string path)
    {
        try { return File.Exists(path) ? Load(path) : null; }
        catch { return null; }
    }

 /// <summary>Reads a table file. Throws if it is malformed.</summary>
    public static NpcRetailVitals Load(string path) => Parse(File.ReadAllBytes(path), path);

 /// <summary>Parses a table from its JSON form. Throws if it is malformed.</summary>
    internal static NpcRetailVitals Parse(ReadOnlySpan<byte> json, string source = "NpcRetailVitals")
    {
        var file = JsonSerializer.Deserialize<VitalsFile>(json)
                   ?? throw new InvalidDataException(source + " is empty");
        var rows = new Dictionary<(uint, int), (int, int)>(file.Assets.Count);
        foreach (var r in file.Assets)
        {
            if (r.MaxHp <= 0 || r.MaxSp < 0 || r.MaxSp > ushort.MaxValue)
                throw new InvalidDataException($"{source}: asset {r.Asset} level {r.Level} has maxHp {r.MaxHp} maxSp {r.MaxSp}");
            if (!rows.TryAdd((r.Asset, r.Level), (r.MaxHp, r.MaxSp)))
                throw new InvalidDataException($"{source}: asset {r.Asset} level {r.Level} is listed twice");
        }
        var byLevel = new Dictionary<int, int>(file.MaxSpByLevel.Count);
        foreach (var l in file.MaxSpByLevel)
        {
            if (l.Value < 0 || l.Value > ushort.MaxValue)
                throw new InvalidDataException($"{source}: level {l.Level} has maxSp {l.Value}");
            if (!byLevel.TryAdd(l.Level, l.Value))
                throw new InvalidDataException($"{source}: level {l.Level} is listed twice");
        }
        return new NpcRetailVitals(rows, byLevel);
    }

 /// <summary>Max HP and max SP for <paramref name="asset"/> at <paramref name="level"/>, when the table has an entry.</summary>
    public bool TryGet(uint asset, int level, out int maxHp, out int maxSp)
    {
        if (_byAssetLevel.TryGetValue((asset, level), out var v))
        {
            (maxHp, maxSp) = v;
            return true;
        }
        maxHp = maxSp = 0;
        return false;
    }

 /// <summary>
 /// The most common max SP for NPCs of <paramref name="level"/> across the table, used as a fallback for an asset
 /// without its own entry.
 /// </summary>
    public bool TryGetLevelMaxSp(int level, out int maxSp) => _maxSpByLevel.TryGetValue(level, out maxSp);

    private sealed class VitalsFile
    {
        [JsonPropertyName("assets")] public List<AssetRow> Assets { get; set; } = new();
        [JsonPropertyName("maxSpByLevel")] public List<LevelRow> MaxSpByLevel { get; set; } = new();
    }

    private sealed class AssetRow
    {
        [JsonPropertyName("asset")] public uint Asset { get; set; }
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("maxHp")] public int MaxHp { get; set; }
        [JsonPropertyName("maxSp")] public int MaxSp { get; set; }
    }

    private sealed class LevelRow
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("value")] public int Value { get; set; }
    }
}
