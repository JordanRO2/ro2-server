using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>
/// Pure row selectors for RMI 3030 (world event trigger): which world event rows are the initial stages, and which
/// event-group rows a world entry or a stage change sends for a map. The byte builders stay in the host.
/// </summary>
public static class WorldEventSelectors
{
 /// <summary>Event type of a red system notice. The data value is a string id.</summary>
    public const byte EventTypeNotice = 3;

 /// <summary>Event type of a background music change. The data value is the music file number.</summary>
    public const byte EventTypeBgm = 4;

 /// <summary>Event type of a map effect.</summary>
    public const byte EventTypeMapEffect = 5;

 /// <summary>Event type of a yellow announcement. The data value is a string id.</summary>
    public const byte EventTypeAnnouncement = 6;

 /// <summary>
 /// Each world event with its initial stage, ordered by event id.
 /// </summary>
 /// <remarks>
 /// The initial stage is the row with a return stage of 0. It is the only row without a fallback stage, and it waits on its check group instead of timing out.
 /// </remarks>
    public static IReadOnlyList<(uint WorldEventId, byte StageLevel)> InitialStages(IEnumerable<WorldEventMain> rows)
        => rows.Where(r => r.Return_Stage == 0)
               .GroupBy(r => r.World_Event_ID)
               .OrderBy(g => g.Key)
               .Select(g => (WorldEventId: g.Key, StageLevel: g.Select(r => r.Stage_Level).Min()))
               .ToList();

 /// <summary>
 /// The records a world entry onto a map carries for one event: that map's announcement rows, then its music rows, in table order.
 /// </summary>
 /// <remarks>
 /// Returns an empty list off the event's maps. Notices of other types appear only in a stage change.
 /// </remarks>
    public static IReadOnlyList<WorldEventEventGroup> WorldEntryRecords(IEnumerable<WorldEventEventGroup> stageRows, uint mapId)
    {
        var rows = stageRows.Where(r => r.Map_Index == mapId).ToList();
        return rows.Where(r => r.Event_Type == EventTypeAnnouncement)
            .Concat(rows.Where(r => r.Event_Type == EventTypeBgm))
            .ToList();
    }

 /// <summary>
 /// The announcement rows of the stage that name a map other than <paramref name="mapId"/>. A player off that map still receives them after the world-entry passes.
 /// </summary>
    public static IReadOnlyList<WorldEventEventGroup> OffMapAnnouncements(IEnumerable<WorldEventEventGroup> stageRows, uint mapId)
        => stageRows.Where(r => r.Event_Type == EventTypeAnnouncement && r.Map_Index != mapId).ToList();

 /// <summary>
 /// The records a stage change sends to a player on <paramref name="mapId"/>: that map's rows of the new stage, in table order.
 /// </summary>
 /// <remarks>
 /// Returns an empty list when the stage names nothing for that map. Types 1 and 2 are not sent.
 /// </remarks>
    public static IReadOnlyList<WorldEventEventGroup> StageChangeRecords(IEnumerable<WorldEventEventGroup> stageRows, uint mapId)
        => stageRows.Where(r => r.Map_Index == mapId
                && r.Event_Type is EventTypeNotice or EventTypeBgm or EventTypeMapEffect or EventTypeAnnouncement)
            .ToList();
}
