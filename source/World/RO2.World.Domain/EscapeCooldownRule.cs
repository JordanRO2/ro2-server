using System.Globalization;
using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>
/// Pure rules of the /escape cooldown (1677 ReqTeleportSavePoint): the cooldown length from Config_Def, the
/// remaining-wait math, and the release-ownership check. The host keeps the live per-character registry and clock;
/// it passes the "now" (unix seconds) and the stored last-escape time in.
/// </summary>
/// <remarks>
/// Home note: this type is not placed by the DDD map; World.Domain is the default (B5 S18). A refused escape does
/// not restart the cooldown — it is counted from the last escape that was not refused.
/// </remarks>
public static class EscapeCooldownRule
{
 /// <summary>Config_Def type of the escape cooldown length, in minutes.</summary>
    public const ushort ConfigEscapeAvailableTime = 81;

 /// <summary>
 /// The cooldown length from Config_Def type 81, in minutes. <see cref="TimeSpan.Zero"/> (no cooldown) when the row
 /// is absent or unparseable.
 /// </summary>
    public static TimeSpan Length(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == ConfigEscapeAvailableTime);
        return row != null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.Zero;
    }

 /// <summary>
 /// The whole seconds still to wait before an escape may be claimed at <paramref name="nowUnix"/>, given the
 /// <paramref name="previous"/> accepted escape time (null = none) and the cooldown <paramref name="length"/>.
 /// Zero means the escape is allowed now.
 /// </summary>
    public static long RemainingSeconds(long? previous, TimeSpan length, long nowUnix)
    {
        var remaining = previous is { } p ? p + (long)length.TotalSeconds - nowUnix : 0;
        return remaining > 0 ? remaining : 0;
    }

 /// <summary>
 /// Whether the stored last-escape time <paramref name="last"/> (null = none recorded) is exactly the claim made at
 /// <paramref name="claimedUnix"/> — i.e. a release may undo it (a later claim that replaced it is left alone).
 /// </summary>
    public static bool IsClaim(long? last, long claimedUnix) => last is { } l && l == claimedUnix;
}
