using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Tracks which talk objectives of a quest the player has met in this login. This covers quests with several talk
/// NPCs, and single-talk quests whose talk NPC is not their reward NPC. A meeting is recorded when the player opens
/// the NPC's dialog (1698 NfyMeetDialogNPC, handled by MeetDialogNPCHandler).
/// </summary>
/// <remarks>
/// Met rows are stored on the quest's document with its other closes and restored at login. They are cleared when the
/// quest is accepted again, and purged once the player no longer holds the quest.
/// </remarks>
internal static class QuestTalkProgress
{
 /// <summary>Quest_Info Complete_Type of a talk objective (Complete_Type_Value1 = the NPC to talk to).</summary>
    internal const byte TalkObjective = 1;

 /// <summary>The met row indexes (into <c>GameDataStore.QuestRows</c>) of each tracked quest.</summary>
    private static readonly ConditionalWeakTable<PlayerState, Dictionary<uint, HashSet<int>>> Table = new();

 /// <summary>The quests whose run began in this login (<see cref="Begin"/>): a 1374 accept or a level-up grant.</summary>
    private static readonly ConditionalWeakTable<PlayerState, HashSet<uint>> Begun = new();

 /// <summary>
 /// Per loaded GameDataStore: the NPC_IDs that are NPCInfo rows and are placed by ZoneSpawn.json, that is, the NPCs
 /// this server can spawn.
 /// </summary>
    private static readonly ConditionalWeakTable<GameDataStore, FrozenSet<uint>> Spawnable = new();

 /// <summary>The result of meeting one NPC for one quest.</summary>
 /// <param name="AllMet">True when, after this meeting, every tracked talk row of the quest is met.</param>
    internal readonly record struct Meeting(bool AllMet);

 /// <summary>
 /// The indexes of the talk rows tracked for a quest: its talk rows with Complete_Type_Value3 0 when there are at least
 /// two of them, or the single talk row of a quest whose NPC is not its reward NPC. Otherwise none.
 /// </summary>
 /// <remarks>
 /// A single-talk quest whose turn-in NPC differs from its talk NPC cannot be turned in until the talk NPC has been met
 /// in this login. Meeting the talk NPC closes its row, as it does for a multi-talk quest. A talk row that names the
 /// reward NPC is left to the turn-in, which meets it.
 /// </remarks>
    internal static int[] TrackedRows(QuestData[] rows)
    {
        var tracked = new List<int>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].Complete_Type == TalkObjective && rows[i].Complete_Type_Value3 == 0 && rows[i].Complete_Type_Value1 != 0)
                tracked.Add(i);
        if (tracked.Count >= 2) return tracked.ToArray();
        if (tracked.Count == 1 && tracked[0] == 0 && rows.Skip(1).All(r => r.Complete_Type == 0)
            && rows[0].Complete_Type_Value1 != rows[0].Reward_NPC_ID)
            return tracked.ToArray();
        return [];
    }

 /// <summary>
 /// Records that the player met <paramref name="npcAsset"/> for <paramref name="questId"/>. Returns null when the quest
 /// has no tracked row for that NPC, and nothing is recorded in that case.
 /// </summary>
    internal static Meeting? Meet(PlayerState player, uint questId, QuestData[] rows, uint npcAsset)
    {
        var tracked = TrackedRows(rows);
        if (tracked.Length == 0) return null;

        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest)
        {
            var targets = tracked.Where(i => rows[i].Complete_Type_Value1 == npcAsset).ToArray();
            if (targets.Length == 0) return null;
            if (!byQuest.TryGetValue(questId, out var met))
            {
                met = [];
                byQuest[questId] = met;
            }
            met.UnionWith(targets);
            return new Meeting(tracked.All(met.Contains));
        }
    }

 /// <summary>
 /// A fresh run of the quest (a 1374 accept or a level-up grant): forgets its met rows, and records that it was taken
 /// in this login.
 /// </summary>
    internal static void Begin(PlayerState player, uint questId)
    {
        Reset(player, questId);
        var begun = Begun.GetOrCreateValue(player);
        lock (begun) begun.Add(questId);
    }

 /// <summary>Forget the quest's met rows (part of <see cref="Begin"/>).</summary>
    internal static void Reset(PlayerState player, uint questId)
    {
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest) byQuest.Remove(questId);
    }

 /// <summary>
 /// Whether the player's run of <paramref name="questId"/> began in this login. False for a quest carried over a relog,
 /// whose earlier closes are known only through QuestProgressStore.PersistedStateKnown.
 /// </summary>
    internal static bool AcceptedThisLogin(PlayerState player, uint questId)
    {
        if (!Begun.TryGetValue(player, out var begun)) return false;
        lock (begun) return begun.Contains(questId);
    }

 /// <summary>
 /// Whether <paramref name="npcId"/> is an NPC a player can meet here: an NPCInfo row that ZoneSpawn.json places on a map.
 /// </summary>
    internal static bool IsMeetableNpc(GameDataStore store, uint npcId) =>
        npcId != 0 && Spawnable.GetValue(store, s => s.MapSpawns.Values
            .SelectMany(spawns => spawns)
            .Select(e => e.AssetIndex)
            .Where(s.Npcs.ContainsKey)
            .ToFrozenSet()).Contains(npcId);

 /// <summary>The row indexes of the quest met in this login or restored at login. Empty when none.</summary>
    internal static IReadOnlySet<int> MetRows(PlayerState player, uint questId)
    {
        if (!Table.TryGetValue(player, out var byQuest)) return new HashSet<int>();
        lock (byQuest)
            return byQuest.TryGetValue(questId, out var met) ? new HashSet<int>(met) : new HashSet<int>();
    }

 /// <summary>
 /// Restores, at login, the met rows of <paramref name="questId"/> that a stored document records. Only the rows this
 /// tracker follows for the quest are restored.
 /// </summary>
    internal static void Restore(PlayerState player, uint questId, QuestData[] rows, IEnumerable<int> metRowIndexes)
    {
        var tracked = TrackedRows(rows);
        if (tracked.Length == 0) return;
        var restored = metRowIndexes.Where(i => Array.IndexOf(tracked, i) >= 0).ToArray();
        if (restored.Length == 0) return;
        var byQuest = Table.GetOrCreateValue(player);
        lock (byQuest)
        {
            if (!byQuest.TryGetValue(questId, out var met))
                byQuest[questId] = met = [];
            met.UnionWith(restored);
        }
    }

 /// <summary>Drops the rows of every quest the player no longer holds.</summary>
    internal static void Purge(PlayerState player)
    {
        if (!Table.TryGetValue(player, out var byQuest)) return;
        lock (byQuest)
        {
            foreach (var questId in byQuest.Keys.ToArray())
                if (!player.ActiveQuests.Contains(questId)) byQuest.Remove(questId);
        }
    }
}
