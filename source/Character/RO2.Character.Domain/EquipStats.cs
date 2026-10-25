using RO2.Characters.Domain;
namespace RO2.Characters.Domain;

/// <summary>
/// Worn-equipment trait bonuses split by <c>Trait_Kind</c>, keyed by <c>Trait_Type</c> (= the 2498
/// stat id). <see cref="Flat"/> holds the summed Kind-1 amounts; <see cref="Percent"/> holds the
/// summed Kind-2 amounts already converted to percentage points (Value/100).
/// </summary>
/// <param name="Flat">Summed Kind-1 flat amounts, by stat id.</param>
/// <param name="Percent">Summed Kind-2 amounts in percentage points, by stat id.</param>
public readonly record struct EquipStats(
    IReadOnlyDictionary<byte, int> Flat,
    IReadOnlyDictionary<byte, float> Percent)
{
    private static readonly Dictionary<byte, int> NoFlat = new();
    private static readonly Dictionary<byte, float> NoPct = new();

    /// <summary>An empty bonus set (no worn gear / no game data).</summary>
    public static EquipStats Empty => new(NoFlat, NoPct);

    /// <summary>Flat bonus for a stat id (0 if none).</summary>
    public int FlatOf(byte statId) => Flat.GetValueOrDefault(statId);

    /// <summary>Percent bonus (in percentage points) for a stat id (0 if none).</summary>
    public float PercentOf(byte statId) => Percent.GetValueOrDefault(statId);

    /// <summary>Applies this set's flat then percent bonus for <paramref name="statId"/> to a base
    /// value: <c>(base + flat) · (1 + percent/100)</c>, rounded.</summary>
    public int Apply(byte statId, int baseValue)
        => (int)Math.Round((baseValue + FlatOf(statId)) * (1f + PercentOf(statId) / 100f));
}
