using RO2.GameData;

namespace RO2.Dungeons.Domain;

/// <summary>The daily entry-count window and the daily bind-reset instant of the instance dungeons.</summary>
public static class DungeonEntryWindow
{
 /// <summary>Retail's daily reset instant as a UTC time of day.</summary>
    public static readonly TimeSpan ResetTimeOfDayUtc = TimeSpan.FromHours(10);

 /// Daily bind-reset instant (NfyDungeonBindReset), as a UTC time of day.
    public static readonly TimeSpan BindResetTimeOfDayUtc = TimeSpan.FromHours(16);

 /// <summary>The start of the daily window <paramref name="utcNow"/> falls in: the latest 10:00 UTC at or before it.</summary>
    public static DateTime WindowStart(DateTime utcNow)
    {
        var utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var today = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc) + ResetTimeOfDayUtc;
        return utc >= today ? today : today.AddDays(-1);
    }

 /// <summary>The first reset instant strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextResetAfter(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(utc.Date, TimeSpan.Zero) + BindResetTimeOfDayUtc;
        return today > utc ? today : today.AddDays(1);
    }

 /// <summary>DungeonInfo rows with a Bind_Value, in DungeonID order.</summary>
    public static List<uint> BoundDungeonIds(GameDataStore gameData) =>
        gameData.DungeonInfo
            .Where(d => !string.IsNullOrWhiteSpace(d.Bind_Value)
                        && !d.Bind_Value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            .Select(d => (uint)d.DungeonID)
            .OrderBy(id => id)
            .ToList();
}
