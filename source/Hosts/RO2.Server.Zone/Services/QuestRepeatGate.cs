using RO2.Quests.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The daily-quest rule checked when a quest is accepted. A daily quest (Daily_Quest) can be accepted once per day,
/// the day starting at <see cref="DailyResetUtc"/>.
/// </summary>
/// <remarks>
/// A daily quest completed at or after the latest daily reset is refused until the next reset. Quest_Term cooldowns
/// are not modelled. Other accept refusals are handled by <see cref="QuestAcceptGates"/>.
/// </remarks>
internal static class QuestRepeatGate
{
 /// <summary>The daily reset time, 13:00 UTC.</summary>
    internal static readonly TimeSpan DailyResetUtc = QuestRepeatRule.DailyResetUtc;

 /// <summary>The Quest_Info repeat columns, which GameDataStore.QuestData does not load.</summary>
    internal sealed record Row(uint Quest_ID, byte Daily_Quest, byte Quest_Frequency, uint Quest_Term);

    private static readonly Lazy<IReadOnlyDictionary<uint, Row>?> Rows = new(Load);

    private static IReadOnlyDictionary<uint, Row>? Load()
    {
        try
        {
            
            var path = Path.Combine("data", "Quest_Info.json");
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Row>>(File.ReadAllBytes(path));
            return rows is null ? null : Index(rows);
        }
        catch
        {
            return null; 
        }
    }

 /// <summary>Indexes the rows by quest, keeping the first row of each quest, as GameDataStore.Quests does.</summary>
    internal static IReadOnlyDictionary<uint, Row> Index(IEnumerable<Row> rows)
    {
        var byId = new Dictionary<uint, Row>();
        foreach (var r in rows) byId.TryAdd(r.Quest_ID, r);
        return byId;
    }

 /// <summary>The quest's repeat columns, or null when the table is unreadable or has no such quest.</summary>
    internal static Row? Of(uint questId) => Rows.Value is { } rows && rows.TryGetValue(questId, out var row) ? row : null;

 /// <summary>The most recent daily reset at or before <paramref name="nowUtc"/>.</summary>
    internal static DateTime LatestDailyReset(DateTime nowUtc) => QuestRepeatRule.LatestDailyReset(nowUtc);

 /// <summary>True when a daily quest last completed at <paramref name="lastCompletedUtc"/> may not be taken again at
 /// <paramref name="nowUtc"/> (see <see cref="QuestRepeatRule.DoneToday"/>).</summary>
    internal static bool DoneToday(DateTime? lastCompletedUtc, DateTime nowUtc) =>
        QuestRepeatRule.DoneToday(lastCompletedUtc, nowUtc);
}
