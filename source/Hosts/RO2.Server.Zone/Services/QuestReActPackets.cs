using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2784 NtyUpdateQuestReAct: the completion record of a repeatable quest, sent directly before the 2429
/// OnQuestPrize that pays it.
/// </summary>
/// <remarks>
/// Body layout: [u32 21][u64 character GID][u32 questId][u64 Unix time in UTC seconds][u8 completion count]. The
/// completion count includes the current completion and is capped at the quest's Quest_Frequency. The time is the
/// moment of the payout.
/// </remarks>
internal static class QuestReActPackets
{
 /// <summary>The blob length the U32 envelope carries: 8 + 4 + 8 + 1.</summary>
    private const uint BlobLength = 21;

 /// <summary>
 /// The 25-byte 2784 body. <paramref name="completionCount"/> is the count including this completion,
 /// already capped by the caller at the quest's <c>Quest_Frequency</c>.
 /// </summary>
    internal static byte[] BuildUpdateQuestReAct2784(ulong characterGid, uint questId, ulong unixSeconds, byte completionCount)
    {
        using var writer = new PacketWriter(25);
        writer.WriteUInt32(BlobLength);
        writer.WriteUInt64(characterGid);     
        writer.WriteUInt32(questId);          
        writer.WriteUInt64(unixSeconds);      
        writer.WriteByte(completionCount);    
        return writer.ToArray();
    }
}

/// <summary>
/// The Quest_Info columns that GameDataStore.QuestData does not load but the quest paths need: Reward_Auto,
/// Quest_Frequency, and the per-row Fail_Type and Fail_Type_Value1. Read once from the data directory.
/// </summary>
/// <remarks>
/// Reward_Auto and Quest_Frequency are meaningful on a quest's first row only. Fail_Type is per row. Rows keep file
/// order, so row i here is row i of GameDataStore.QuestRows.
/// </remarks>
internal static class QuestInfoExtraColumns
{
    internal sealed record Row(uint Quest_ID, byte Reward_Auto, byte Quest_Frequency, byte Fail_Type, uint Fail_Type_Value1);

    private static readonly Lazy<IReadOnlyDictionary<uint, Row[]>?> RowsById = new(Load);

    private static IReadOnlyDictionary<uint, Row[]>? Load()
    {
        try
        {
            var path = Path.Combine("data", "Quest_Info.json");
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Row>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            var groups = new Dictionary<uint, List<Row>>();
            foreach (var r in rows)
            {
                if (!groups.TryGetValue(r.Quest_ID, out var list)) groups[r.Quest_ID] = list = new List<Row>(1);
                list.Add(r);
            }
            return groups.ToDictionary(g => g.Key, g => g.Value.ToArray());
        }
        catch
        {
            return null; 
        }
    }

 /// <summary>Every row of the quest in file order, or null when the table or the quest is unknown.</summary>
    internal static Row[]? Rows(uint questId) =>
        RowsById.Value is { } all && all.TryGetValue(questId, out var rows) ? rows : null;

 /// <summary>The quest's first row (the one that carries Reward_Auto and Quest_Frequency), or null.</summary>
    internal static Row? First(uint questId) => Rows(questId) is { Length: > 0 } rows ? rows[0] : null;

 /// <summary>True when the quest pays itself the moment its objective closes (<c>Reward_Auto</c> 1).</summary>
    internal static bool IsRewardAuto(uint questId) => First(questId) is { Reward_Auto: 1 };
}
