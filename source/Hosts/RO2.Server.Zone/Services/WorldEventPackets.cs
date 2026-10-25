using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for RMI 3030 (world event trigger): the stage of a world event and the notices for the recipient's map.
/// </summary>
/// <remarks>
/// The body is a u32 byte count, then a blob with the event id, a shape byte, the stage level, a record count, and the records.
/// Shape 1 sets the stage with no records. Shape 2 carries records, each with an event type, a map index and two data values.
/// Event types: 3 red system notice, 4 background music, 5 map effect, 6 yellow notice.
/// </remarks>
internal static class WorldEventPackets
{
 /// <summary>Body length of the 3030 record-less form, excluding the count field.</summary>
    internal const uint NoRecordsBlobLen = 8;

 /// <summary>Total body length of the record-less form, in bytes.</summary>
    internal const int NoRecordsBodyLen = 12;

 /// <summary>Shape byte of a stage set with no records.</summary>
    internal const byte ShapeNoRecords = 1;

 /// <summary>Shape byte of a stage set followed by records.</summary>
    internal const byte ShapeRecords = 2;

 /// <summary>Length of one record, in bytes.</summary>
    internal const int RecordLength = 13;

 /// <summary>Event type of a red system notice. The data value is a string id.</summary>
    internal const byte EventTypeNotice = 3;

 /// <summary>Event type of a background music change. The data value is the music file number.</summary>
    internal const byte EventTypeBgm = 4;

 /// <summary>Event type of a map effect.</summary>
    internal const byte EventTypeMapEffect = 5;

 /// <summary>Event type of a yellow announcement. The data value is a string id.</summary>
    internal const byte EventTypeAnnouncement = 6;

 /// <summary>Stage level sent to a player who is not on any map of the event.</summary>
    internal const byte OffMapStage = 0;

 /// <summary>
 /// Number of times the full world-entry pass is repeated on each map load.
 /// </summary>
    internal const int WorldEntryPasses = 2;

 /// <summary>
 /// Builds the record-less 3030 body that sets an event's stage.
 /// </summary>
    public static byte[] BuildWorldEventTrigger3030(uint worldEventId, byte stageLevel)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(NoRecordsBlobLen); 
        pw.WriteUInt32(worldEventId);     
        pw.WriteByte(ShapeNoRecords);     
        pw.WriteByte(stageLevel);         
        pw.WriteUInt16(0);                
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 3030 body that sets an event's stage and carries its records.
 /// </summary>
    public static byte[] BuildWorldEventRecords3030(uint worldEventId, byte stageLevel, IReadOnlyList<WorldEventEventGroup> rows)
    {
        if (rows.Count == 0) throw new ArgumentException("the record form carries at least one record", nameof(rows));
        using var pw = new PacketWriter(12 + RecordLength * rows.Count);
        pw.WriteUInt32((uint)(NoRecordsBlobLen + RecordLength * rows.Count));
        pw.WriteUInt32(worldEventId);
        pw.WriteByte(ShapeRecords);
        pw.WriteByte(stageLevel);
        pw.WriteUInt16((ushort)rows.Count);
        foreach (var r in rows)
        {
            pw.WriteByte(r.Event_Type);
            pw.WriteUInt32(r.Map_Index);
            pw.WriteUInt32(r.Type_Data1);
            pw.WriteUInt32(r.Type_Data2);
        }
        return pw.ToArray();
    }

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

 /// <summary>
 /// The 3030 bodies of one world-entry burst onto a map, in send order.
 /// </summary>
 /// <param name="events">Each event's id, live stage and the stage's rows, in ascending id order.</param>
 /// <param name="mapId">The map being entered.</param>
 /// <remarks>
 /// Two passes are sent. Each pass has one record-less frame per event, followed by a record frame when the event has rows for this map.
 /// After both passes, each event with off-map announcements gets one record frame.
 /// </remarks>
    public static IReadOnlyList<byte[]> BuildWorldEntryBurst(
        IReadOnlyList<(uint WorldEventId, byte StageLevel, IReadOnlyList<WorldEventEventGroup> StageRows)> events, uint mapId)
    {
        var bodies = new List<byte[]>();
        for (int pass = 0; pass < WorldEntryPasses; pass++)
            foreach (var (id, stage, rows) in events)
            {
                bodies.Add(BuildWorldEventTrigger3030(id, stage));
                var onMap = WorldEntryRecords(rows, mapId);
                if (onMap.Count > 0) bodies.Add(BuildWorldEventRecords3030(id, stage, onMap));
            }
        foreach (var (id, stage, rows) in events)
        {
            var notices = OffMapAnnouncements(rows, mapId);
            if (notices.Count > 0) bodies.Add(BuildWorldEventRecords3030(id, stage, notices));
        }
        return bodies;
    }

 /// <summary>
 /// The 3030 bodies a stage change sends to one player on a map: the new stage and its records for that map.
 /// </summary>
 /// <remarks>
 /// When the stage names nothing for that map, the player receives one record-less frame with stage 0.
 /// </remarks>
    public static IReadOnlyList<byte[]> BuildStageChangePush(uint worldEventId, byte stageLevel,
        IEnumerable<WorldEventEventGroup> stageRows, uint mapId)
    {
        var records = StageChangeRecords(stageRows, mapId);
        if (records.Count == 0) return new[] { BuildWorldEventTrigger3030(worldEventId, OffMapStage) };
        return new[]
        {
            BuildWorldEventTrigger3030(worldEventId, stageLevel),
            BuildWorldEventRecords3030(worldEventId, stageLevel, records),
        };
    }
}
