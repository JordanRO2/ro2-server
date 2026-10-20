using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Gates a 1374 quest-accept request must pass before the quest is inserted: the Job_Master range, PC_Job and PC_ProJob
/// (each refused with a 2431), then the start item for Start_Type 2 quests (refused with a 2421).
/// </summary>
/// <remarks>
/// The gates are evaluated in this order. Job_Master compares the base level (or the master level, for
/// Job_Master_Quest 1) with the range from Min to Max, where a Max of 0 means no upper bound. PC_Job requires the
/// mask bit for the character's class (Job_ID - 1), or a mask of 0. PC_ProJob requires the character's profession to
/// equal the required value. The master level is always 0 on this server, so quests that need a master level of at
/// least 1 are refused. Start_Type 2 quests need their start item in the bag. A Need_Quest_ID gate is not implemented.
/// </remarks>
internal static class QuestAcceptGates
{
 /// <summary>Quest_Info Start_Type of a quest started from an item (Start_Type_ID is the item).</summary>
    internal const byte StartFromItem = 2;

 /// <summary>The Quest_Info.json gate columns. GameDataStore's QuestData does not carry PC_ProJob or the Job_Master
 /// columns.</summary>
    internal sealed record Row(uint Quest_ID, uint PC_Job, byte PC_ProJob, byte Job_Master_Min_Level,
        byte Job_Master_Max_Level, byte Job_Master_Quest);

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

 /// <summary>FIRST row per quest, like GameDataStore.Quests: the gate columns live on a quest's first row.</summary>
    internal static IReadOnlyDictionary<uint, Row> Index(IEnumerable<Row> rows)
    {
        var byId = new Dictionary<uint, Row>();
        foreach (var r in rows) byId.TryAdd(r.Quest_ID, r);
        return byId;
    }

 /// <summary>The quest's gate columns, or null when the table is unreadable or has no such quest.</summary>
    internal static Row? Of(uint questId) => Rows.Value is { } rows && rows.TryGetValue(questId, out var row) ? row : null;

 /// <summary>
 /// Returns the 2431 code that refuses a 1374 for the quest of <paramref name="row"/>, or null when every gate passes.
 /// Checks run in the order Job_Master, PC_Job, PC_ProJob.
 /// </summary>
 /// <param name="combatClass">The character's class byte.</param>
 /// <param name="profession">The character's profession byte.</param>
 /// <param name="baseLevel">The live base level.</param>
 /// <param name="masterLevel">The master level.</param>
    internal static uint? Refusal(Row row, byte combatClass, byte profession, byte baseLevel, byte masterLevel)
    {
        byte v = row.Job_Master_Quest == 1 ? masterLevel : baseLevel;
        if (v < row.Job_Master_Min_Level || (row.Job_Master_Max_Level != 0 && v >= row.Job_Master_Max_Level))
            return QuestProgressPackets.QuestErrorJobMasterLevel;
        if (!JobAllows(row.PC_Job, combatClass)) return QuestProgressPackets.QuestErrorJobMismatch;
        if (row.PC_ProJob != 0 && row.PC_ProJob != profession) return QuestProgressPackets.QuestErrorProJobMismatch;
        return null;
    }

 /// <summary>A PC_Job mask of 0 lets every class; otherwise bit (class - 1) must be set. A class outside 1..32
 /// (Novice 100, or 0 when unknown) has no bit.</summary>
    internal static bool JobAllows(uint pcJob, byte combatClass) =>
        pcJob == 0 || (combatClass is >= 1 and <= 32 && (pcJob & (1u << (combatClass - 1))) != 0);

 /// <summary>The item a Start_Type 2 quest needs in the bag to be accepted, or 0 for any other Start_Type.</summary>
    internal static uint StartItemOf(QuestData quest) => quest.Start_Type == StartFromItem ? quest.Start_Type_ID : 0;
}
