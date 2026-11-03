using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Pays a dungeon finder run when its list's last boss dies: the 2816 completion notice and the reward items,
/// money and mail that go with it. Called right after the boss's corpse event.
/// </summary>
/// <remarks>
/// <para>
/// Only the player who started the run is paid, and only once. The run is the one recorded by
/// <see cref="DungeonFinderPendingStore"/> when the finder entry was made.
/// </para>
/// <para>
/// Items are added to the bag first, then the money is credited, then the 2816 is sent. Reward rows are chosen
/// by the character's level from Find_Dungeon_Reward.
/// </para>
/// <para>
/// Job mastery points from the reward are not granted, because the server does not track them.
/// </para>
/// <para>
/// Rewards that do not fit in the bag are mailed as system mails. In that case the 2816 state is 3. If no mail
/// can be stored, nothing is paid and no 2816 is sent. The run ends in either case.
/// </para>
/// </remarks>
public static class DungeonFinderReward
{
 /// <summary>
 /// Pays <paramref name="killer"/>'s finder run when <paramref name="npcId"/> is the list's last boss.
 /// </summary>
    public static async Task TryPayAsync(ProudSession session, PlayerState killer, uint npcId, GameDataStore data,
        IServiceProvider services, ILogger logger)
    {
        var run = DungeonFinderPendingStore.GetRun(killer);
        if (run is null) return;
        var list = data.FindDungeonList.FirstOrDefault(r => r.List_ID == run.ListId);
        if (list is null || list.Last_Boss_ID != npcId) return;
        if (killer.MapId != run.ArrivalMapId)
        {
            logger.LogInformation("{Name} killed finder boss {Npc} of list {List} on map {Map}, not the run's map {Arrival}: not paid",
                killer.Name, npcId, run.ListId, killer.MapId, run.ArrivalMapId);
            return;
        }
        if (!DungeonFinderPendingStore.TryEndRun(killer, run)) return;   

        var result = await services.GetRequiredService<PayFinderRun>().PayAsync(killer.CharacterId, killer.Name,
            killer.Level, ProJobLevel(killer), list, killer, data.FindDungeonCategory, data.FindDungeonReward, data.Items);

        var row = result.Row;
        foreach (var item in result.SkippedItems)
            logger.LogWarning("{Name}: finder reward row {Row} names item {Item}, which is not in ItemInfo; skipped", killer.Name, row?.ID, item);
        switch (result.Outcome)
        {
            case PayOutcome.NoRow:
                logger.LogWarning("{Name} finished finder list {List}: no Find_Dungeon_Reward row for level {Level}, no 2816",
                    killer.Name, run.ListId, result.Level);
                break;
            case PayOutcome.NotPaid:
                logger.LogWarning("{Name} finished finder list {List} with no bag room for reward row {Row}, and no reward mail could be stored: not paid, no 2816",
                    killer.Name, run.ListId, row!.ID);
                break;
            case PayOutcome.Mailed:
                logger.LogInformation("{Name} finished finder list {List} with a full bag: reward row {Row} mailed ({Mails} mails, {Money} zeny credited; {Exp} job-mastery points not modelled)",
                    killer.Name, run.ListId, row!.ID, result.Mails, row.Money, row.Exp);
                break;
            default:
                logger.LogInformation("{Name} finished finder list {List}: reward row {Row} ({Items} items, {Money} zeny; {Exp} job-mastery points not modelled)",
                    killer.Name, run.ListId, row!.ID, result.Items, row.Money, row.Exp);
                break;
        }
    }

 /// <summary>The profession level added to the character level for the reward row; this server models none, so 0.</summary>
    private static int ProJobLevel(PlayerState player)
        => player.Character is { } c ? CharacterSerializer.WireProJob(c).Level : 0;

 /// <summary>The level that selects the reward row.</summary>
    public static int RewardLevel(PlayerState player) => RewardPolicy.RewardLevel(player.Level, ProJobLevel(player));
}
