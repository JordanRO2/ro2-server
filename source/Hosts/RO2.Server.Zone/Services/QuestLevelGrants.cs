using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Quests the server starts by itself when a quest payout raises the base level: Quest_Info rows with
/// <c>Start_Type</c> 4 (start at level <c>Start_Type_ID</c>), inserted with an unrequested 2421 after the payout's
/// 2394 burst.
/// </summary>
/// <remarks>
/// A payout level-up grants every Start_Type-4 quest whose base-level and profession-level ranges the character
/// now meets, unless it is already held or completed. Only the quest payout calls this, so level-ups from kill EXP
/// grant nothing.
/// </remarks>
internal static class QuestLevelGrants
{
 /// <summary>Quest_Info Start_Type of a quest the server starts at a base level (Start_Type_ID).</summary>
    internal const byte LevelStartType = 4;

 /// <summary>The profession-level columns of a Quest_Info row, which QuestData does not carry.</summary>
    internal sealed record ProJobRow(uint Quest_ID, byte MIN_ProJob_Level, byte MAX_ProJob_Level);

    private static readonly Lazy<IReadOnlyDictionary<uint, ProJobRow>?> ProJobRows = new(Load);

    private static IReadOnlyDictionary<uint, ProJobRow>? Load()
    {
        try
        {
            
            var path = Path.Combine("data", "Quest_Info.json");
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<ProJobRow>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            var byId = new Dictionary<uint, ProJobRow>();
            foreach (var r in rows) byId.TryAdd(r.Quest_ID, r); 
            return byId;
        }
        catch
        {
            return null; 
        }
    }

 /// <summary>
 /// True when <paramref name="quest"/> is a Start_Type-4 quest whose start level, base-level range and
 /// profession-level range (<paramref name="proJob"/>) the character meets at <paramref name="level"/> and job level
 /// <paramref name="jobLevel"/>. A MAX of 0 is read as no upper bound.
 /// </summary>
    internal static bool Meets(QuestData quest, ProJobRow proJob, byte level, byte jobLevel) =>
        quest.Start_Type == LevelStartType
        && level >= quest.Start_Type_ID
        && level >= quest.MIN_Base_Level && (quest.MAX_Base_Level == 0 || level <= quest.MAX_Base_Level)
        && jobLevel >= proJob.MIN_ProJob_Level && (proJob.MAX_ProJob_Level == 0 || jobLevel <= proJob.MAX_ProJob_Level);

 /// <summary>
 /// After a payout's EXP: when <paramref name="player"/>'s level is above <paramref name="levelBefore"/>, inserts
 /// each Start_Type-4 quest it now meets (<see cref="Meets"/>) and neither holds nor has completed, and sends its
 /// 2421. Nothing when the level did not rise, or when the profession columns cannot be read.
 /// </summary>
    internal static async Task GrantAsync(ProudSession session, PlayerState player, byte levelBefore,
        IServiceProvider services, ILogger logger)
    {
        if (player.Level <= levelBefore || ProJobRows.Value is not { } proJobs) return;
        var store = services.GetRequiredService<GameDataStore>();
        var jobLevel = player.Character?.JobLevel ?? 0;
        var due = store.Quests.Values
            .Where(q => q.Start_Type == LevelStartType && !player.ActiveQuests.Contains(q.Quest_ID)
                        && proJobs.TryGetValue(q.Quest_ID, out var p) && Meets(q, p, player.Level, jobLevel))
            .OrderBy(q => q.Quest_ID)
            .ToList();
        if (due.Count == 0) return;

        var quests = services.GetRequiredService<IQuestRepository>();
        var completed = await quests.GetCompletedQuestIdsAsync(player.CharacterId);
        foreach (var quest in due)
        {
            if (completed.Contains(quest.Quest_ID) || player.ActiveQuests.Contains(quest.Quest_ID)) continue;
            await quests.AcceptQuestAsync(player.CharacterId, quest.Quest_ID);
            QuestAcceptHandler.BeginRun(player, quest.Quest_ID);
            await QuestAcceptHandler.StoreTrackedRunStateAsync(quests, player, quest.Quest_ID, store, logger);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInsert,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestInsert2421(quest.Quest_ID)));
            logger.LogInformation("{Name} reached level {Level}: quest {QuestId} granted (Start_Type 4)",
                player.Name, player.Level, quest.Quest_ID);
        }
    }
}
