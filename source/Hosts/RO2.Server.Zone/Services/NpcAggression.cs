using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RO2.Server.Zone.Services;

/// <summary>Whether, and on what condition, a monster starts a fight with a player who has not hit it.</summary>
public enum NpcAggressionMode
{
    /// <summary>Passive: never aggros by proximity, only retaliates when struck (the default for every asset).</summary>
    None = 0,

    /// <summary>Aggros any player that enters its sight radius, regardless of level.</summary>
    Always = 1,

    /// <summary>Aggros a player in its sight radius only while the player is not too far above it in level
    /// (<c>playerLevel - npcLevel &lt;= LevelGap</c>): the classic "stops noticing you once you out-level it" rule.</summary>
    ByLevel = 2,
}

/// <summary>
/// Per-asset monster aggression: whether a hostile NPC opens a fight on its own (and on what level condition).
/// </summary>
/// <remarks>
/// <para>
/// SERVER-SIDE, CUSTOM CONTENT — not shipped game data. The NPCInfo table carries a reaction/sight
/// radius (<see cref="RO2.GameData.NpcData.Reaction_Bound"/>) but NO aggression flag: <c>Camp == 2</c> only
/// means "hostile faction / attackable" (the passive Poring family is Camp 2), and no NPCInfo column separates
/// aggressive from passive monsters. That information belongs to server-side mob/AI data, which this project
/// does not ship — the same situation as monster drops.
/// </para>
/// <para>
/// So aggression is CUSTOM: this table overlays the world like <c>custom/data/</c> does, and when the file is
/// absent EVERY asset is <see cref="NpcAggressionMode.None"/> (passive). That is the current behaviour — monsters
/// only retaliate — so a missing table regresses nothing. The table is meant to be hand-authored; the sight
/// radius itself stays the shipped <c>Reaction_Bound</c>, only the WHETHER is here.
/// </para>
/// <para>
/// File (<see cref="ShippedPath"/>): <c>{ "rows": [ { "asset": 11004320, "mode": "byLevel", "levelGap": 10 }, … ] }</c>.
/// <c>mode</c> is <c>none</c> / <c>always</c> / <c>byLevel</c> (case-insensitive); <c>levelGap</c> applies to
/// <c>byLevel</c> only.
/// </para>
/// </remarks>
public sealed class NpcAggression
{
    /// <summary>The custom table, relative to the server's working directory (custom/ overlays data/).</summary>
    public static readonly string ShippedPath = Path.Combine("custom", "data", "NpcAggression.json");

    /// <summary>One asset's aggression rule.</summary>
    public readonly record struct Row(uint Asset, NpcAggressionMode Mode, int LevelGap);

    private readonly FrozenDictionary<uint, Row> _byAsset;

    private NpcAggression(IEnumerable<Row> rows) => _byAsset = rows.ToFrozenDictionary(r => r.Asset);

    /// <summary>Number of assets with an aggression rule.</summary>
    public int Count => _byAsset.Count;

    /// <summary>The assets' rows.</summary>
    public IEnumerable<Row> Rows => _byAsset.Values;

    /// <summary>The rule for <paramref name="asset"/>, or a <see cref="NpcAggressionMode.None"/> row when it has none.</summary>
    public bool TryGet(uint asset, out Row row) => _byAsset.TryGetValue(asset, out row);

    /// <summary>
    /// Whether a monster of <paramref name="mode"/> acquires a player of <paramref name="playerLevel"/> who entered
    /// its sight. A pure decision, so it is unit-tested without a table: <see cref="NpcAggressionMode.Always"/>
    /// always, <see cref="NpcAggressionMode.ByLevel"/> while the player is at most <paramref name="levelGap"/> levels
    /// above the monster, <see cref="NpcAggressionMode.None"/> never.
    /// </summary>
    public static bool Acquires(NpcAggressionMode mode, int levelGap, int npcLevel, int playerLevel) => mode switch
    {
        NpcAggressionMode.Always => true,
        NpcAggressionMode.ByLevel => playerLevel - npcLevel <= levelGap,
        _ => false,
    };

    private static readonly Lazy<NpcAggression?> s_shipped = new(() => TryLoad(ShippedPath));

    /// <summary>The custom table, or null when the file is missing or unreadable (every asset is then passive).</summary>
    public static NpcAggression? Shipped => s_shipped.Value;

    /// <summary><see cref="Load"/>, or null on any failure (a missing or malformed file leaves every asset passive).</summary>
    public static NpcAggression? TryLoad(string path)
    {
        try { return File.Exists(path) ? Load(path) : null; }
        catch { return null; }
    }

    /// <summary>Reads a table file. Throws on a malformed file, an unknown mode, a negative level gap, or a
    /// duplicate asset.</summary>
    public static NpcAggression Load(string path)
    {
        var file = JsonSerializer.Deserialize<AggressionFile>(File.ReadAllBytes(path))
                   ?? throw new InvalidDataException(path + " is empty");
        var rows = new List<Row>(file.Rows.Count);
        var seen = new HashSet<uint>();
        foreach (var r in file.Rows)
        {
            var mode = ParseMode(r.Mode, r.Asset);
            if (r.LevelGap < 0)
                throw new InvalidDataException($"aggression asset {r.Asset}: level gap {r.LevelGap} is negative");
            if (!seen.Add(r.Asset))
                throw new InvalidDataException($"aggression asset {r.Asset} is listed twice");
            if (mode != NpcAggressionMode.None)
                rows.Add(new Row(r.Asset, mode, r.LevelGap));
        }
        return new NpcAggression(rows);
    }

    private static NpcAggressionMode ParseMode(string? mode, uint asset) => (mode ?? "none").Trim().ToLowerInvariant() switch
    {
        "none" or "" => NpcAggressionMode.None,
        "always" => NpcAggressionMode.Always,
        "bylevel" => NpcAggressionMode.ByLevel,
        _ => throw new InvalidDataException($"aggression asset {asset}: unknown mode '{mode}'"),
    };

    private sealed class AggressionFile
    {
        [JsonPropertyName("rows")] public List<RowFile> Rows { get; set; } = new();
    }

    private sealed class RowFile
    {
        [JsonPropertyName("asset")] public uint Asset { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "none";
        [JsonPropertyName("levelGap")] public int LevelGap { get; set; }
    }
}
