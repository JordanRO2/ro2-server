using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Worlds.Domain;

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
                var onMap = WorldEventSelectors.WorldEntryRecords(rows, mapId);
                if (onMap.Count > 0) bodies.Add(BuildWorldEventRecords3030(id, stage, onMap));
            }
        foreach (var (id, stage, rows) in events)
        {
            var notices = WorldEventSelectors.OffMapAnnouncements(rows, mapId);
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
        var records = WorldEventSelectors.StageChangeRecords(stageRows, mapId);
        if (records.Count == 0) return new[] { BuildWorldEventTrigger3030(worldEventId, OffMapStage) };
        return new[]
        {
            BuildWorldEventTrigger3030(worldEventId, stageLevel),
            BuildWorldEventRecords3030(worldEventId, stageLevel, records),
        };
    }
}
