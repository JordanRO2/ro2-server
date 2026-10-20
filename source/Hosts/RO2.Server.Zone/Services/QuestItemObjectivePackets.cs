using System.Runtime.CompilerServices;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2428 OnQuestInCompleteNotify: the progress tick of an item objective that is met by holding or using an
/// item, rather than by picking it up (2424 is the pick-up tick).
/// </summary>
/// <remarks>
/// The body has the layout [u16 selfLen 18][u16 msgTag 58301][u32 0][u32 questId][u32 item template id][u16 count],
/// the same as the 2424 body with another msgTag. Complete_Type 6 ("use item") ticks follow the item-use request;
/// Complete_Type 4 ("collect") ticks follow a quest accept or a count drop back to 0.
/// </remarks>
internal static class QuestItemObjectivePackets
{
 /// <summary>The 2428 msgTag, at body offset 2.</summary>
    internal const ushort MsgTagQuestInComplete = 58301;

 /// <summary>The 20-byte 2428 body, built through <see cref="PacketWriter.WriteCompactBABody"/>.</summary>
    internal static byte[] BuildQuestInCompleteNotify2428(uint questId, uint itemTemplateId, ushort count)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestInComplete);
            p.WriteUInt32(0);              
            p.WriteUInt32(questId);        
            p.WriteUInt32(itemTemplateId); 
            p.WriteUInt16(count);          
        });
        return pw.ToArray();
    }
}

/// <summary>
/// Per-row use counts of the Complete_Type 6 ("use item") rows of the quests a player holds.
/// </summary>
/// <remarks>
/// PlayerState.QuestKillProgress holds one counter per quest, but a quest can have several use-item rows with
/// different items, so the counts are kept per row index of GameDataStore.QuestRows. The table lives as long as
/// the PlayerState. Counts are saved with the quest's progress document on each tick and restored at login.
/// Accepting a quest clears its counts.
/// </remarks>
internal static class QuestUseItemProgress
{
    private static readonly ConditionalWeakTable<PlayerState, Dictionary<uint, int[]>> Table = new();

 /// <summary>Forgets the quest's row counts (a fresh accept).</summary>
    internal static void Reset(PlayerState player, uint questId)
    {
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest) byQuest.Remove(questId);
    }

 /// <summary>
 /// Adds one use to a row, capped at <paramref name="needed"/>. Returns the row's count after the use, or null when
 /// the row was already met.
 /// </summary>
    internal static int? AddUse(PlayerState player, uint questId, int rowCount, int rowIndex, int needed)
    {
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest)
        {
            if (!byQuest.TryGetValue(questId, out var counts) || counts.Length != rowCount)
                byQuest[questId] = counts = new int[rowCount];
            if (counts[rowIndex] >= needed) return null;
            counts[rowIndex] = Math.Min(counts[rowIndex] + 1, needed);
            return counts[rowIndex];
        }
    }

 /// <summary>Sets a row's use count at login, from its stored progress. An index outside the quest's rows is ignored.</summary>
    internal static void Restore(PlayerState player, uint questId, int rowCount, int rowIndex, int count)
    {
        if (rowIndex < 0 || rowIndex >= rowCount) return;
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest)
        {
            if (!byQuest.TryGetValue(questId, out var counts) || counts.Length != rowCount)
                byQuest[questId] = counts = new int[rowCount];
            counts[rowIndex] = Math.Max(0, count);
        }
    }

 /// <summary>The use count of row <paramref name="rowIndex"/> of the quest (0 when never used).</summary>
    internal static int Count(PlayerState player, uint questId, int rowIndex)
    {
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest)
            return byQuest.TryGetValue(questId, out var counts) && rowIndex < counts.Length ? counts[rowIndex] : 0;
    }
}
