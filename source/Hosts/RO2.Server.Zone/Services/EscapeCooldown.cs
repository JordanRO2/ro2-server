using System.Globalization;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Cooldown for the /escape command (1677 ReqTeleportSavePoint). After one escape, another within the cooldown is refused
/// with 3010 AnsTeleportSavePoint (<see cref="TeleportSavePointPackets"/>) and does not warp.
/// </summary>
/// <remarks>
/// <para>
/// The cooldown length comes from Config_Def type 81, in minutes. A refused escape does not restart the cooldown, which is
/// counted from the last escape that was not refused.
/// </para>
/// <para>
/// The state is kept in memory per character. It survives a relog but not a server restart.
/// </para>
/// </remarks>
public static class EscapeCooldown
{
 /// <summary>Config_Def type of the escape cooldown length, in minutes.</summary>
    internal const ushort ConfigEscapeAvailableTime = 81;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<uint, long> LastEscapeUnix = new();

 /// <summary>
 /// The cooldown length from Config_Def type 81, in minutes. <see cref="TimeSpan.Zero"/> (no cooldown) when the row is
 /// absent or unparseable.
 /// </summary>
    internal static TimeSpan Length(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == ConfigEscapeAvailableTime);
        return row != null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.Zero;
    }

 /// <summary>
 /// Claims an escape for <paramref name="characterId"/> at <paramref name="nowUnix"/>. Returns true and records the escape
 /// when none was made within <paramref name="length"/>; otherwise returns false with the whole seconds still to wait.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The check and the claim are atomic, so two concurrent requests cannot both warp. A claim whose warp then fails is
 /// undone with <see cref="Release"/>.
 /// </para>
 /// </remarks>
    internal static bool TryClaim(uint characterId, long nowUnix, TimeSpan length, out long remainingSeconds, out long? previous)
    {
        lock (Gate)
        {
            previous = LastEscapeUnix.TryGetValue(characterId, out var last) ? last : null;
            remainingSeconds = previous is { } p ? p + (long)length.TotalSeconds - nowUnix : 0;
            if (remainingSeconds > 0) return false;
            remainingSeconds = 0;
            LastEscapeUnix[characterId] = nowUnix;
            return true;
        }
    }

 /// <summary>
 /// Undoes the <see cref="TryClaim"/> made at <paramref name="claimedUnix"/> when the warp did not happen, restoring
 /// <paramref name="previous"/>. A later claim that replaced it is left alone.
 /// </summary>
    internal static void Release(uint characterId, long claimedUnix, long? previous)
    {
        lock (Gate)
        {
            if (!LastEscapeUnix.TryGetValue(characterId, out var last) || last != claimedUnix) return;
            if (previous is { } p) LastEscapeUnix[characterId] = p;
            else LastEscapeUnix.Remove(characterId);
        }
    }
}
