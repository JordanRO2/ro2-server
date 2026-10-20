using System.Collections.Frozen;
using System.Text.Json;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The two Skill_Table_SkillLevel columns the 1509 / 2718 path needs and the curated
/// <c>SkillLevelData</c> model does not carry: <c>Number_Of_Normal_Influences</c> (how many damage
/// records one NPC element of a 2718 holds) and <c>Skill_Damage_Quantity</c> (how many NPCs one cast
/// may damage). Read once, by skill id (group*100+level), from the same
/// <c>data/Skill_Table_SkillLevel.json</c> GameDataStore loads, the way SkillDamagePackets reads
/// Skill_Type from Skill_Table_Base.
/// </summary>
/// <remarks>
/// A skill id the table does not know, or a table that could not be read (<see cref="TableError"/>),
/// has no row: one record per element and no quantity limit, the behaviour before these columns were
/// read.
/// </remarks>
internal static class SkillLevelColumns
{
 /// <summary>
 /// The two columns of one Skill_Table_SkillLevel row, as the table holds them (0 not yet raised to
 /// 1).
 /// </summary>
    internal readonly record struct Row(int NormalInfluences, int DamageQuantity);

 /// <summary>
 /// The file GameDataStore loads Skill_Table_SkillLevel from, resolved against the same working
 /// directory (Program.cs hands <c>LoadAsync</c> the relative directory "data"; SkillDamagePackets
 /// reads Skill_Table_Base the same way).
 /// </summary>
    private static readonly string ShippedTable = Path.Combine("data", "Skill_Table_SkillLevel.json");

    private static readonly Lazy<(FrozenDictionary<uint, Row> Rows, string? Error)> Shipped = new(LoadShipped);

 /// <summary>Why the table could not be read, or null when it was.</summary>
    internal static string? TableError => Shipped.Value.Error;

 /// <summary>
 /// How many damage records each NPC element of this skill's 2718 carries: max(1,
 /// Number_Of_Normal_Influences), or 1 for a skill the table does not know (see the class remarks).
 /// </summary>
    internal static int NormalInfluencesOf(uint skillId) => NormalInfluencesOf(Shipped.Value.Rows, skillId);

 /// <inheritdoc cref="NormalInfluencesOf(uint)"/>
    internal static int NormalInfluencesOf(IReadOnlyDictionary<uint, Row> rows, uint skillId)
        => rows.TryGetValue(skillId, out var row) ? Math.Max(1, row.NormalInfluences) : 1;

 /// <summary>
 /// The most NPCs one cast of this skill may damage: max(1, Skill_Damage_Quantity), or null for a
 /// skill the table does not know.
 /// </summary>
    internal static int? DamageQuantityOf(uint skillId) => DamageQuantityOf(Shipped.Value.Rows, skillId);

 /// <inheritdoc cref="DamageQuantityOf(uint)"/>
    internal static int? DamageQuantityOf(IReadOnlyDictionary<uint, Row> rows, uint skillId)
        => rows.TryGetValue(skillId, out var row) ? Math.Max(1, row.DamageQuantity) : null;

 /// <summary>
 /// One damage record's amount when a cast's full damage is split over <paramref name="records"/>
 /// records: <c>max(1, round(full / records))</c>, so the element's records together carry the
 /// skill's DamageRate once.
 /// </summary>
 /// <remarks>
 /// Each record does NOT carry the full rate. CombatService's defence mitigation is proportional and
 /// its crit doubles, so dividing the mitigated (and possibly doubled) hit equals dividing the base
 /// up to rounding.
 /// </remarks>
    internal static int RecordDamage(int fullDamage, int records)
        => Math.Max(1, (int)Math.Round((double)fullDamage / Math.Max(1, records), MidpointRounding.AwayFromZero));

 /// <summary>
 /// Skill_Table_Base.Influence_Target_Type of the Detection skills, whose hit damages nothing.
 /// </summary>
    internal const int DetectionInfluenceTargetType = 9;

 /// <summary>
 /// True when the skill's group has Skill_Table_Base.Influence_Target_Type 9 (6321 Detection and
 /// 60203, the GM's Detection): its 2718 lists each NPC with its vitals and NO damage record, and
 /// the NPC loses no HP.
 /// </summary>
 /// <remarks>An unknown group or an unreadable table reads as false (damage as before).</remarks>
    internal static bool DealsNoDamage(uint skillId)
        => ShippedInfluenceTargetTypes.Value.TryGetValue(skillId / 100, out var type) && type == DetectionInfluenceTargetType;

 /// <summary>
 /// Skill_Group -&gt; Influence_Target_Type for every Skill_Table_Base row that carries both.
 /// </summary>
    internal static FrozenDictionary<uint, int> ParseInfluenceTargetTypes(ReadOnlySpan<byte> skillTableBaseJson)
    {
        var reader = new Utf8JsonReader(skillTableBaseJson, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var types = new Dictionary<uint, int>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Skill_Group", out var g) && g.ValueKind == JsonValueKind.Number && g.TryGetUInt32(out var group)
                && row.TryGetProperty("Influence_Target_Type", out var t) && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var type))
                types[group] = type;
        }
        return types.ToFrozenDictionary();
    }

    private static readonly string ShippedBaseTable = Path.Combine("data", "Skill_Table_Base.json");

    private static readonly Lazy<FrozenDictionary<uint, int>> ShippedInfluenceTargetTypes = new(() =>
    {
        try
        {
            return ParseInfluenceTargetTypes(File.ReadAllBytes(ShippedBaseTable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return FrozenDictionary<uint, int>.Empty;
        }
    });

 /// <summary>
 /// ID -&gt; the two columns, for every row that carries an ID. A row missing a column reads it as
 /// 0.
 /// </summary>
    internal static FrozenDictionary<uint, Row> Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var rows = new Dictionary<uint, Row>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("ID", out var id) || !id.TryGetUInt32(out var skillId))
                continue;
            rows[skillId] = new Row(IntOf(row, "Number_Of_Normal_Influences"), IntOf(row, "Skill_Damage_Quantity"));
        }
        return rows.ToFrozenDictionary();
    }

    private static int IntOf(JsonElement row, string column)
        => row.TryGetProperty(column, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static (FrozenDictionary<uint, Row>, string?) LoadShipped()
    {
        try
        {
            var rows = Parse(File.ReadAllBytes(ShippedTable));
            return rows.Count > 0 ? (rows, null) : (rows, $"{ShippedTable} has no rows");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenDictionary<uint, Row>.Empty, $"{ShippedTable} could not be read: {ex.Message}");
        }
    }
}
