using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Quests.Application;
using RO2.Quests.Domain;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1377 ReqQuestComplete: reports a quest objective as done at its NPC.</summary>
/// <remarks>
/// A talk quest is closed (2427) and stays active until its turn-in (1378), which pays it. Quests with
/// <c>Reward_Auto</c> are paid immediately. A request naming quest 0 checks the "reach a place" objectives of the
/// held quests against the player's position and closes those the player stands in.
/// </remarks>
public class QuestCompleteHandler : ZoneRmiHandlerBase<QuestCompleteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestComplete;
    public override string Name => "QuestComplete";

 /// <summary>Quest_Info Complete_Type of a "reach a place" objective (Complete_Type_Value1 = a Position_Info id).</summary>
    private const byte PositionObjective = 3;

    public QuestCompleteHandler(IServiceScopeFactory s, ILogger<QuestCompleteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var questId = reader.ReadUInt32();
        uint? giverId = payload.Length >= 18 ? reader.ReadUInt32() : null;

        var store = services.GetRequiredService<GameDataStore>();

        if (questId == 0)
        {
            await CheckPositionObjectivesAsync(session, player, store, services);
            return;
        }

        
        
        var useCase = services.GetRequiredService<QuestCompleteUseCase>();
        player.QuestKillProgress.TryGetValue(questId, out var progress);
        var decision = useCase.Decide(new QuestCompleteRequest(questId, player.ActiveQuests.Contains(questId), progress,
            QuestInfoExtraColumns.IsRewardAuto(questId)));

        switch (decision.Verdict)
        {
            case QuestCompleteVerdict.UnknownQuest:
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        questId, QuestProgressPackets.QuestErrorInsertRejected)));
                return;
            case QuestCompleteVerdict.NotHeld:
                Logger.LogWarning("{Name} tried to complete non-active quest {QuestId}", player.Name, questId);
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        questId, QuestProgressPackets.QuestErrorPrizeRejected)));
                return;
        }

        var quest = decision.Quest!;
        var payNow = decision.PayNow;
        var sendUpdate = decision.SendUpdate;
        var closesNow = decision.ClosesNow;

        switch (decision.Counter)
        {
            case QuestCounterChange.Forget:
                player.ActiveQuests.Remove(questId);
                player.QuestKillProgress.Remove(questId);
                LootService.ResetExtraKillProgress(player, questId);
                QuestUseItemProgress.Reset(player, questId);
                break;
            case QuestCounterChange.Set:
                player.QuestKillProgress[questId] = decision.CounterValue;
                break;
        }

        if (sendUpdate)
        {
            var updateTarget = giverId ?? quest.Complete_Type_Value1;
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestUpdateNotify,
                writer => writer.WriteBytes(QuestNotifyPackets.BuildQuestUpdateNotify2432(questId, updateTarget)));
        }

        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
            writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));

        
        
        
        
        if (closesNow && QuestAcceptHandler.ObjectiveCloseGrants(questId).Count > 0)
            await QuestPrizeHandler.GrantObjectiveCloseItemsAsync(session, player, questId, store,
                services.GetRequiredService<IInventoryRepository>(), Logger);

        if (!payNow)
        {
            
            
            
            if (quest.Complete_Type == ObjectiveRules.TalkObjective)
                await QuestProgressStore.SaveObjectiveStateAsync(services.GetRequiredService<IQuestRepository>(),
                    player, questId, store, Logger);
            Logger.LogInformation("{Name} closed quest {QuestId} at {Giver}; paid at its turn-in",
                player.Name, questId, giverId);
            return;
        }

        await useCase.CompleteAsync(player.CharacterId, questId);
        
        await QuestPrizeHandler.GrantRewardsAsync(
            session, player, quest, QuestPrizeHandler.NoRewardChoice, services, Logger);
        Logger.LogInformation("{Name} quest {QuestId}: paid on the spot (Reward_Auto)", player.Name, questId);
    }

 /// <summary>
 /// Handles a 1377 naming quest 0: closes the held "reach a place" quests whose place the player stands in
 /// and refuses the others.
 /// </summary>
    private async Task CheckPositionObjectivesAsync(ProudSession session, PlayerState player,
        GameDataStore store, IServiceProvider services)
    {
        var refused = new List<uint>();
        var reached = new List<QuestData>();
        var anyClosedBefore = false;
        
        foreach (var questId in player.ActiveQuests.ToArray())
        {
            if (!store.Quests.TryGetValue(questId, out var quest) || quest.Complete_Type != PositionObjective)
                continue;
            
            
            if (!store.Positions.TryGetValue(quest.Complete_Type_Value1, out var place)) continue;
            player.QuestKillProgress.TryGetValue(questId, out var progress);
            if (progress >= 1)
            {
                anyClosedBefore = true;
                continue;
            }
            if (IsInside(player, place)) reached.Add(quest);
            else refused.Add(questId);
        }

        if (refused.Count == 0 && reached.Count == 0)
        {
            if (anyClosedBefore)
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        0, QuestProgressPackets.QuestErrorPrizeRejected)));
            return;
        }

        
        
        
        
        var closes = new List<(QuestData Quest, bool Pay)>();
        foreach (var quest in reached)
        {
            var questId = quest.Quest_ID;
            if (QuestInfoExtraColumns.IsRewardAuto(questId))
            {
                if (!player.ActiveQuests.Remove(questId)) continue;
                player.QuestKillProgress.Remove(questId);
                LootService.ResetExtraKillProgress(player, questId);
                QuestUseItemProgress.Reset(player, questId);
                closes.Add((quest, true));
            }
            else
            {
                player.QuestKillProgress[questId] = 1;
                closes.Add((quest, false));
            }
        }

        foreach (var questId in refused)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorRequirementUnmet)));
        }
        if (refused.Count > 0)
            Logger.LogDebug("{Name} is not yet at the place of quest(s) {Quests} ({Map} {X:F1},{Y:F1})",
                player.Name, string.Join(",", refused), player.MapId, player.X, player.Y);

        var useCase = services.GetRequiredService<QuestCompleteUseCase>();
        foreach (var (quest, pay) in closes)
        {
            var questId = quest.Quest_ID;
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));
            if (!pay)
            {
                Logger.LogInformation("{Name} reached the place of quest {QuestId}; paid at its turn-in",
                    player.Name, questId);
                continue;
            }

            await useCase.CompleteAsync(player.CharacterId, questId);
            await QuestPrizeHandler.GrantRewardsAsync(
                session, player, quest, QuestPrizeHandler.NoRewardChoice, services, Logger);
            Logger.LogInformation("{Name} quest {QuestId}: place reached, paid on the spot (Reward_Auto)",
                player.Name, questId);
        }
    }

 /// <summary>Whether the player stands in <paramref name="place"/>: on its map and within its radius, height ignored.</summary>
    private static bool IsInside(PlayerState player, PositionData place)
    {
        if (player.MapId != place.Area_ID) return false;
        var dx = player.X - place.Position_X;
        var dy = player.Y - place.Position_Y;
        return dx * dx + dy * dy <= place.Radius * place.Radius;
    }
}
