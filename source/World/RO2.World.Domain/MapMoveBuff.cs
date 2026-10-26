using System.Globalization;
using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>
/// Map-move buff: the effect applied to a player after each map load, and its duration. Both values come from Config_Def.
/// </summary>
/// <remarks>
/// The buff's effect row is Effect_Table 60205. Its stat modifiers are applied on top of the player's current stats, and
/// a warp departure removes it. Applied by the host EffectService.ApplyMapMoveBuffAsync.
/// </remarks>
public static class MapMoveBuff
{
 /// <summary>Config_Def type of the map-move buff's effect id.</summary>
    public const ushort ConfigEffectId = 65;

 /// <summary>Config_Def type of the map-move buff's duration, in seconds.</summary>
    public const ushort ConfigDurationSeconds = 66;

 /// <summary>
 /// The map-move buff's effect id and duration in milliseconds, or null when either config row is missing or is not a
 /// positive whole number. In that case no buff is applied.
 /// </summary>
    public static (uint EffectId, ulong DurationMs)? Of(IReadOnlyList<ConfigDef> config)
    {
        if (Positive(config, ConfigEffectId) is not { } effectId
            || Positive(config, ConfigDurationSeconds) is not { } seconds)
            return null;
        return ((uint)effectId, (ulong)seconds * 1000UL);
    }

    private static long? Positive(IReadOnlyList<ConfigDef> config, ushort type)
    {
        var row = config.FirstOrDefault(c => c.type == type);
        return row != null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
               && v > 0 && v <= uint.MaxValue
            ? v
            : null;
    }
}
