using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>
/// The effect-table rule of a cross-map warp: which effects a warp ends. The host keeps the per-store cache and every
/// effect removal and trait-change line; this holds only the rule.
/// </summary>
public static class WarpEffectRules
{
 /// <summary>The effect table's Warp_Handling value of an effect that a warp ends.</summary>
    public const byte WarpHandlingEnds = 0;

 /// <summary>True when an effect with this Warp_Handling is ended by a warp.</summary>
    public static bool EndsOnWarp(byte warpHandling) => warpHandling == WarpHandlingEnds;

 /// <summary>The ids of the effects in <paramref name="effectTable"/> that a warp ends.</summary>
    public static HashSet<uint> EndsOnWarpIds(IEnumerable<EffectTable> effectTable)
    {
        var ids = new HashSet<uint>();
        foreach (var row in effectTable)
        {
            if (EndsOnWarp(row.Warp_Handling))
                ids.Add(row.ID);
        }
        return ids;
    }
}
