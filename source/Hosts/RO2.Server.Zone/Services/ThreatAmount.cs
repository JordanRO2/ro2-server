using System.Collections.Frozen;
using System.Text.Json;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Threat banked on a monster's aggro table by one damage application:
/// (damage * max(1, AddAggroRate) + AddAggro) * (1 + stanceRate / 10000), truncated.
/// AddAggro and AddAggroRate come from the skill level. The stance rate is not yet applied to players, so it is always 0.
/// </summary>
/// <remarks>
/// Normal hits bank their damage. Skills with AddAggro or AddAggroRate bank the modified value.
/// Stance effects (trait 49) are not modelled.
/// </remarks>
internal static class ThreatAmount
{
 /// <summary>Trait id of StanceAggroRate (Trait_Kind 4, in 1/10,000).</summary>
    internal const uint StanceAggroRateTraitId = 49;

 /// <summary>AddAggro of a normal hit: 0.</summary>
    internal const int NormalHitAddAggro = 0;

 /// <summary>AddAggroRate of a normal hit, or of a skill level with no row: 0.</summary>
    internal const int NormalHitAddAggroRate = 0;

 /// <summary>
 /// Threat for a damage application with an AddAggroRate of 0. Negative damage counts as 0;
 /// a stance rate at or below -100% gives 0.
 /// </summary>
    internal static long Compute(long damage, int addAggro, double stanceRate)
        => Compute(damage, addAggro, NormalHitAddAggroRate, stanceRate);

 /// <summary>
 /// Threat for a damage application. <paramref name="aggroRate"/> multiplies the damage before AddAggro is added.
 /// Negative damage counts as 0; a stance rate at or below -100% gives 0.
 /// </summary>
    internal static long Compute(long damage, int addAggro, int aggroRate, double stanceRate)
    {
        var scaled = (Math.Max(0, damage) * Math.Max(1, aggroRate) + addAggro) * (1 + stanceRate / 10000.0);
        return scaled > 0 ? (long)scaled : 0;
    }

 /// <summary>
 /// Threat banked by a player's damage application, using the stance rate of its active effects.
 /// </summary>
    internal static long For(EffectService? effects, PlayerState player, long damage, int addAggro)
        => For(effects, player, damage, addAggro, NormalHitAddAggroRate);

 /// <summary>
 /// Threat for a skill's damage application, using the skill level's AddAggroRate.
 /// </summary>
    internal static long For(EffectService? effects, PlayerState player, long damage, int addAggro, int aggroRate)
        => Compute(damage, addAggro, aggroRate, effects is null ? 0 : StanceRateOf(effects, player));

 /// <summary>
 /// Sum of the trait-49 stance aggro rate, in 1/10,000, across the player's active effects.
 /// Always 0 today, because no path applies a trait-49 effect to a player.
 /// </summary>
    internal static double StanceRateOf(EffectService effects, PlayerState player)
    {
        ActiveEffect[] active;
        lock (player.ActiveEffects)
            active = player.ActiveEffects.ToArray();
        return active.Length == 0 ? 0 : effects.SumRate(active, StanceAggroRateTraitId);
    }

 /// <summary>
 /// AddAggro of a skill level (group * 100 + level), or 1 when the table does not list the skill or could not be read.
 /// </summary>
    internal static int AddAggroOf(uint skillId) => AddAggroOf(Shipped.Value.Rows, skillId);

 /// <inheritdoc cref="AddAggroOf(uint)"/>
    internal static int AddAggroOf(IReadOnlyDictionary<uint, int> rows, uint skillId)
        => rows.TryGetValue(skillId, out var addAggro) ? addAggro : DefaultAddAggro;

 /// <summary>AddAggro of a skill level with no row: the table's most common value (1).</summary>
    internal const int DefaultAddAggro = 1;

 /// <summary>
 /// AddAggroRate of a skill level, or 0 when the table does not list the skill or could not be read.
 /// </summary>
    internal static int AddAggroRateOf(uint skillId) => AddAggroRateOf(Shipped.Value.Rates, skillId);

 /// <inheritdoc cref="AddAggroRateOf(uint)"/>
    internal static int AddAggroRateOf(IReadOnlyDictionary<uint, int> rates, uint skillId)
        => rates.TryGetValue(skillId, out var rate) ? rate : NormalHitAddAggroRate;

 /// <summary>Why the table could not be read, or null.</summary>
    internal static string? TableError => Shipped.Value.Error;

 /// <summary>Skill level id to AddAggro, read from Skill_Table_SkillLevel.</summary>
    internal static FrozenDictionary<uint, int> Parse(ReadOnlySpan<byte> json) => ParseColumns(json).AddAggro;

 /// <summary>Skill level id to AddAggroRate, read from Skill_Table_SkillLevel.</summary>
    internal static FrozenDictionary<uint, int> ParseRates(ReadOnlySpan<byte> json) => ParseColumns(json).AddAggroRate;

    private static (FrozenDictionary<uint, int> AddAggro, FrozenDictionary<uint, int> AddAggroRate) ParseColumns(
        ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var rows = new Dictionary<uint, int>();
        var rates = new Dictionary<uint, int>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("ID", out var id) || !id.TryGetUInt32(out var skillId))
                continue;
            rows[skillId] = IntColumn(row, "AddAggro");
            rates[skillId] = IntColumn(row, "AddAggroRate");
        }
        return (rows.ToFrozenDictionary(), rates.ToFrozenDictionary());
    }

    private static int IntColumn(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

 /// <summary>The Skill_Table_SkillLevel file loaded by the game data store.</summary>
    private static readonly string ShippedTable = Path.Combine("data", "Skill_Table_SkillLevel.json");

    private static readonly Lazy<(FrozenDictionary<uint, int> Rows, FrozenDictionary<uint, int> Rates, string? Error)> Shipped = new(() =>
    {
        try
        {
            var (rows, rates) = ParseColumns(File.ReadAllBytes(ShippedTable));
            return rows.Count > 0 ? (rows, rates, null) : (rows, rates, $"{ShippedTable} has no rows");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenDictionary<uint, int>.Empty, FrozenDictionary<uint, int>.Empty,
                $"{ShippedTable} could not be read: {ex.Message}");
        }
    });
}
