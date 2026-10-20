using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Quest failures detected on the server: a Fail_Type 26 quest fails when the player dies, and a Fail_Type 2 quest
/// fails when the player has killed the NPC named by its row enough times. Each failure sends OnQuestFailed (2434)
/// and marks the quest failed for the login, so the turn-in refuses it.
/// </summary>
/// <remarks>
/// Fail_Type 38 (time limit) is handled by QuestAcceptHandler. Fail_Types 39, 35 and 36 need escort, protected-NPC
/// and NPC-detection systems that this server does not have, so those quests never fail here.
/// Kill counts are kept per login and reset when the quest is accepted again.
/// </remarks>
internal static class QuestFailTriggers
{
 /// <summary>Quest_Info Fail_Type: killing the NPC Value1 Value2 times fails the quest.</summary>
    internal const byte FailTypeKill = 2;

 /// <summary>Quest_Info Fail_Type: the player dying fails the quest.</summary>
    internal const byte FailTypeDeath = 26;

 /// <summary>The tail of a Fail_Type 26 failure.</summary>
    internal const uint DeathTail = 0;

 /// <summary>The fail columns of one Quest_Info row.</summary>
    internal sealed record FailRow(uint Quest_ID, byte Fail_Type, uint Fail_Type_Value1, uint Fail_Type_Value2);

 /// <summary>One failure to send: the quest, the failing row's type and the 2434 tail.</summary>
    internal readonly record struct Failure(uint QuestId, byte FailType, uint Tail);

    private static readonly Lazy<IReadOnlyDictionary<uint, FailRow[]>> RowsById = new(Load);

 /// <summary>Per login: (quest, row index) -> kills of the row's NPC since the quest was accepted.</summary>
    private static readonly ConditionalWeakTable<PlayerState, ConcurrentDictionary<(uint Quest, int Row), int>> KillCounts = new();

 /// <summary>
 /// Groups the Quest_Info rows by quest, keeping only quests with a Fail_Type 2 or 26 row. Rows keep file order,
 /// so row i matches row i of GameDataStore.QuestRows.
 /// </summary>
    internal static IReadOnlyDictionary<uint, FailRow[]> ParseFailRows(byte[] questInfoJson)
    {
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<FailRow>>(questInfoJson) ?? new List<FailRow>();
        return rows.GroupBy(r => r.Quest_ID)
            .Where(g => g.Any(r => r.Fail_Type is FailTypeKill or FailTypeDeath))
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    private static IReadOnlyDictionary<uint, FailRow[]> Load()
    {
        try
        {
            
            var path = Path.Combine("data", "Quest_Info.json");
            return File.Exists(path) ? ParseFailRows(File.ReadAllBytes(path)) : new Dictionary<uint, FailRow[]>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new Dictionary<uint, FailRow[]>();   
        }
    }

 /// <summary>
 /// The held quests that a death fails: those with a Fail_Type 26 row that have not already failed, ascending,
 /// each with tail <see cref="DeathTail"/>.
 /// </summary>
    internal static List<Failure> PlanDeathFailures(IReadOnlyDictionary<uint, FailRow[]> rows,
        IEnumerable<uint> held, Func<uint, bool> alreadyFailed)
    {
        var plan = new List<Failure>();
        foreach (var questId in held.Distinct().OrderBy(q => q))
        {
            if (alreadyFailed(questId) || !rows.TryGetValue(questId, out var questRows)) continue;
            if (questRows.Any(r => r.Fail_Type == FailTypeDeath))
                plan.Add(new Failure(questId, FailTypeDeath, DeathTail));
        }
        return plan;
    }

 /// <summary>
 /// Counts a kill of <paramref name="npcId"/> against the held quests' Fail_Type 2 rows that name it. Returns the
 /// quests whose count reached the row's Value2 (at least 1), ascending, each with the row's Value1 as tail.
 /// <paramref name="counts"/> is the login's kill table; failed quests are not counted.
 /// </summary>
    internal static List<Failure> CountKill(IReadOnlyDictionary<uint, FailRow[]> rows, IEnumerable<uint> held,
        Func<uint, bool> alreadyFailed, ConcurrentDictionary<(uint Quest, int Row), int> counts, uint npcId)
    {
        var plan = new List<Failure>();
        if (npcId == 0) return plan;
        foreach (var questId in held.Distinct().OrderBy(q => q))
        {
            if (alreadyFailed(questId) || !rows.TryGetValue(questId, out var questRows)) continue;
            for (int i = 0; i < questRows.Length; i++)
            {
                var row = questRows[i];
                if (row.Fail_Type != FailTypeKill || row.Fail_Type_Value1 != npcId) continue;
                var killed = counts.AddOrUpdate((questId, i), 1, (_, n) => n + 1);
                if (killed >= Math.Max(1u, row.Fail_Type_Value2))
                {
                    plan.Add(new Failure(questId, FailTypeKill, row.Fail_Type_Value1));
                    break;   
                }
            }
        }
        return plan;
    }

 /// <summary>Forgets the kill counts of <paramref name="questId"/> (a new accept starts them over).</summary>
    internal static void ResetKillCounts(PlayerState player, uint questId)
    {
        if (!KillCounts.TryGetValue(player, out var counts)) return;
        foreach (var key in counts.Keys.Where(k => k.Quest == questId).ToArray())
            counts.TryRemove(key, out _);
    }

 /// <summary>The player died: fails every held Fail_Type 26 quest and sends OnQuestFailed to the victim.</summary>
    internal static Task OnPlayerDiedAsync(ProudSession session, PlayerState player, ILogger? log, CancellationToken ct = default)
    {
        var plan = PlanDeathFailures(RowsById.Value, player.ActiveQuests.ToArray(), q => QuestFailState.IsFailed(player, q));
        return SendAsync(session, player, plan, log, ct);
    }

 /// <summary>Counts a kill of <paramref name="npcId"/> and fails each held Fail_Type 2 quest whose count it completes.
 /// Sends OnQuestFailed to the killer.</summary>
    internal static Task OnNpcKilledAsync(ProudSession session, PlayerState player, uint npcId, ILogger? log,
        CancellationToken ct = default)
    {
        var rows = RowsById.Value;
        if (rows.Count == 0) return Task.CompletedTask;
        var counts = KillCounts.GetValue(player, static _ => new ConcurrentDictionary<(uint Quest, int Row), int>());
        var plan = CountKill(rows, player.ActiveQuests.ToArray(), q => QuestFailState.IsFailed(player, q), counts, npcId);
        return SendAsync(session, player, plan, log, ct);
    }

    private static async Task SendAsync(ProudSession session, PlayerState player, List<Failure> plan, ILogger? log,
        CancellationToken ct)
    {
        foreach (var f in plan)
        {
            
            QuestFailState.MarkFailed(player, f.QuestId);
            var body = QuestFailPackets.BuildQuestFailed2434(f.QuestId, QuestFailPackets.FailedCode, f.FailType, f.Tail);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestFailed, writer => writer.WriteBytes(body), ct);
            log?.LogInformation("{Name} quest {QuestId} failed (Fail_Type {Type})", player.Name, f.QuestId, f.FailType);
        }
    }
}
