using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1374 ReqQuestInsert: accepts a quest from an NPC.</summary>
public class QuestAcceptHandler : ZoneRmiHandlerBase<QuestAcceptHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestInsert;
    public override string Name => "QuestAccept";

    public QuestAcceptHandler(IServiceScopeFactory s, ILogger<QuestAcceptHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Complete_Type of a timed objective, met after Complete_Type_Value1 milliseconds.</summary>
    private const byte TimedObjective = 34;

 /// <summary>Fail_Type of a time limit (Fail_Type_Value1 = milliseconds).</summary>
    private const byte TimeLimitFailType = 38;

 /// <summary>Complete_Type of a collect objective (Value1 = item, Value2 = count).</summary>
    private const byte CollectObjective = 4;

 /// <summary>
 /// Token of the live timed-objective timer for each quest a player holds. Only the timer that stored the
 /// current token may close the objective, so a re-accepted quest is not closed by an earlier timer.
 /// </summary>
    private static readonly ConditionalWeakTable<PlayerState, ConcurrentDictionary<uint, object>> ObjectiveTimers = new();

 /// <summary>Token of the live time-limit timer for each quest a player holds; see <see cref="ObjectiveTimers"/>.</summary>
    private static readonly ConditionalWeakTable<PlayerState, ConcurrentDictionary<uint, object>> FailTimers = new();

 /// <summary>True when the quest is not repeatable (Quest_Frequency 0), so a character that completed it cannot take it again.</summary>
    private static bool IsOneShot(uint questId) => QuestRepeatGate.Of(questId) is { Quest_Frequency: 0 };

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

        var store = services.GetRequiredService<GameDataStore>();

        if (!store.Quests.TryGetValue(questId, out var quest))
        {
            Logger.LogWarning("{Name} tried to accept unknown quest {QuestId}", player.Name, questId);
            
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorInsertRejected)));
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (player.ActiveQuests.Contains(questId))
        {
            Logger.LogDebug("{Name} asked again for already-active quest {QuestId}; refused", player.Name, questId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorInsertRejected)));
            return;
        }

        var questRepo = services.GetRequiredService<IQuestRepository>();

        
        
        
        
        
        
        
        
        
        
        
        
        
        if (IsOneShot(questId))
        {
            var completed = await questRepo.GetCompletedQuestIdsAsync(player.CharacterId);
            if (completed.Contains(questId))
            {
                Logger.LogInformation("{Name} asked again for completed one-shot quest {QuestId}; ignored",
                    player.Name, questId);
                return;
            }
        }

        
        
        
        
        if (QuestRepeatGate.Of(questId) is { Daily_Quest: > 0 } daily
            && QuestRepeatGate.DoneToday(await questRepo.GetLastCompletedAtAsync(player.CharacterId, questId), DateTime.UtcNow))
        {
            Logger.LogInformation("{Name} asked again for daily quest {QuestId}, already done since the last reset; refused",
                player.Name, questId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorDailyDone, daily.Daily_Quest)));
            return;
        }

        
        
        
        
        
        
        if (QuestAcceptGates.Of(questId) is { } gates && player.Character is { } character)
        {
            var refusal = QuestAcceptGates.Refusal(gates,
                WorldEntryService.CombatJobByte(character), WorldEntryService.CreationProfessionByte(character),
                player.Level, CharacterSerializer.WireProJob(character).Level);
            if (refusal is { } code)
            {
                Logger.LogInformation("{Name} accept of quest {QuestId} refused with {Code} (class / profession / Job_Master gate)",
                    player.Name, questId, code);
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(questId, code)));
                return;
            }
        }

        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var takes = AcceptTakes(questId, store);
        var grants = AcceptGrants(quest);
        var startItem = QuestAcceptGates.StartItemOf(quest);
        IReadOnlyList<InventoryItem>? bag = null;
        if (takes.Count > 0 || grants.Count > 0 || startItem != 0)
            bag = await inventory.GetByCharacterAsync(player.CharacterId);
        
        
        
        if (bag is not null && startItem != 0 && QuestPrizeHandler.CountInBag(bag, startItem) < 1)
        {
            Logger.LogInformation("{Name} accept of quest {QuestId} refused: its start item {Item} is not in the bag",
                player.Name, questId, startItem);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInsert, writer => writer.WriteBytes(
                QuestProgressPackets.BuildQuestInsertResult2421(questId, QuestProgressPackets.QuestInsertStartItemMissing)));
            return;
        }
        
        
        if (bag is not null && takes.Any(t => QuestPrizeHandler.CountInBag(bag, t.ItemId) < t.Count))
        {
            Logger.LogInformation("{Name} accept of quest {QuestId} refused: an accept item is not in the bag", player.Name, questId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInsert, writer => writer.WriteBytes(
                QuestProgressPackets.BuildQuestInsertResult2421(questId, QuestProgressPackets.QuestInsertItemMissing)));
            return;
        }
        
        
        
        
        var grantCells = grants.Count(g => store.Items.ContainsKey(g.ItemId));
        if (bag is not null && grantCells > 0 && BagCapacity.FreeCells(bag, store.Items).Count < grantCells)
        {
            Logger.LogInformation("{Name} accept of quest {QuestId} refused: no room for its {Count} accept item(s)",
                player.Name, questId, grantCells);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInsert, writer => writer.WriteBytes(
                QuestProgressPackets.BuildQuestInsertResult2421(questId, QuestProgressPackets.QuestErrorBagFull)));
            return;
        }

        
        await questRepo.AcceptQuestAsync(player.CharacterId, questId);
        BeginRun(player, questId);
        await StoreTrackedRunStateAsync(questRepo, player, questId, store, Logger);

        Logger.LogInformation("{Name} accepted quest {QuestId} (type: {Type})",
            player.Name, questId, quest.Quest_Type);

        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInsert,
            writer => writer.WriteBytes(QuestProgressPackets.BuildQuestInsert2421(questId)));

        
        
        if (bag is not null)
            foreach (var (itemId, count) in takes)
                await QuestPrizeHandler.RemoveFromBagAsync(session, player, inventory, bag, itemId, count, Logger, services);

        
        await TickHeldCollectItemsAsync(session, player, quest, store, services);

        
        
        await GrantAcceptItemAsync(session, player, quest, store, services);

        
        await StartQuestTimerAsync(session, player, quest, store);
    }

 /// <summary>
 /// Handles a collect quest accepted while its item is already held: sends the held count (2428), and the
 /// completion (2427) when that meets the objective.
 /// </summary>
    private async Task TickHeldCollectItemsAsync(ProudSession session, PlayerState player, QuestData quest,
        GameDataStore store, IServiceProvider services)
    {
        var rows = store.QuestRows.TryGetValue(quest.Quest_ID, out var all) ? all : new[] { quest };
        if (!rows.Any(r => r.Complete_Type == CollectObjective && r.Complete_Type_Value1 != 0)) return;

        var bag = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
        var held = new long[rows.Length];
        var anyHeld = false;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type != CollectObjective || row.Complete_Type_Value1 == 0) continue;
            var needed = QuestPrizeHandler.ObjectiveItemCount(row);
            held[i] = Math.Min(QuestPrizeHandler.CountInBag(bag, row.Complete_Type_Value1), needed);
            if (held[i] == 0) continue;
            anyHeld = true;
            var tickBody = QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(
                quest.Quest_ID, row.Complete_Type_Value1, (ushort)Math.Min(held[i], ushort.MaxValue));
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInCompleteNotify, writer => writer.WriteBytes(tickBody));
        }
        if (!anyHeld) return;

        var objectiveRows = 0;
        var closed = true;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Complete_Type == 0) continue;
            objectiveRows++;
            if (rows[i].Complete_Type != CollectObjective || rows[i].Complete_Type_Value1 == 0
                || held[i] < QuestPrizeHandler.ObjectiveItemCount(rows[i]))
                closed = false;
        }

        if (rows[0].Complete_Type == CollectObjective && rows[0].Complete_Type_Value1 != 0
            && (closed || objectiveRows == 1))
            player.QuestKillProgress[quest.Quest_ID] = (byte)Math.Min(held[0], 255);

        if (closed)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(quest.Quest_ID)));
            Logger.LogInformation("{Name} quest {QuestId}: collect objective already held at accept",
                player.Name, quest.Quest_ID);
            
            
            
            
            await QuestPrizeHandler.GrantObjectiveCloseItemsAsync(session, player, quest.Quest_ID, store,
                services.GetRequiredService<IInventoryRepository>(), Logger);
        }
    }

 /// <summary>
 /// Starts the timers of a quest with a timed objective (Complete_Type 34) or a time limit (Fail_Type 38). The
 /// timed objective closes the quest when its time is up; the time limit fails it. Sends 2433 OnQuestTimerStart.
 /// </summary>
    private async Task StartQuestTimerAsync(ProudSession session, PlayerState player, QuestData quest, GameDataStore store)
    {
        var questId = quest.Quest_ID;
        var rows = store.QuestRows.TryGetValue(questId, out var all) ? all : new[] { quest };
        var timed = rows.FirstOrDefault(r => r.Complete_Type == TimedObjective);
        var limit = QuestInfoExtraColumns.Rows(questId)?.FirstOrDefault(r => r.Fail_Type == TimeLimitFailType);
        if (timed is null && limit is null) return;

        await session.SendRmiAsync(ZoneS2COpcodes.OnQuestTimerStart, writer => writer.WriteUInt32(questId));

        
        if (limit is { Fail_Type_Value1: > 0 })
        {
            var failToken = new object();
            FailTimers.GetOrCreateValue(player)[questId] = failToken;
            var failDelay = TimeSpan.FromMilliseconds((double)limit.Fail_Type_Value1);
            _ = Task.Run(() => ExpireTimeLimitAsync(session, player, questId, limit.Fail_Type_Value1, failDelay, failToken));
            Logger.LogDebug("{Name} quest {QuestId}: time limit started ({Ms} ms)",
                player.Name, questId, limit.Fail_Type_Value1);
        }
        if (timed is null) return;

        var token = new object();
        ObjectiveTimers.GetOrCreateValue(player)[questId] = token;
        var delay = TimeSpan.FromMilliseconds((double)timed.Complete_Type_Value1);
        _ = Task.Run(() => CloseTimedObjectiveAsync(session, player, questId, delay, token));
        Logger.LogDebug("{Name} quest {QuestId}: timed objective started ({Ms} ms)",
            player.Name, questId, timed.Complete_Type_Value1);
    }

 /// <summary>Closes a quest whose timed objective has run out, paying a Reward_Auto quest on the spot.</summary>
    private async Task CloseTimedObjectiveAsync(ProudSession session, PlayerState player, uint questId,
        TimeSpan delay, object token)
    {
        try
        {
            await Task.Delay(delay);
            if (!PlayerManager.TryGetPlayer(session.SessionId, out var current) || !ReferenceEquals(current, player))
                return;
            if (!ObjectiveTimers.TryGetValue(player, out var timers)
                || !timers.TryRemove(new KeyValuePair<uint, object>(questId, token)))
                return;
            if (!player.ActiveQuests.Contains(questId)) return;

            
            player.QuestKillProgress[questId] = 1;
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));
            Logger.LogInformation("{Name} quest {QuestId}: timed objective met", player.Name, questId);

            if (!QuestInfoExtraColumns.IsRewardAuto(questId)) return;
            using var scope = ScopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<GameDataStore>();
            if (!store.Quests.TryGetValue(questId, out var quest)) return;
            
            if (!player.ActiveQuests.Remove(questId)) return;
            player.QuestKillProgress.Remove(questId);
            await services.GetRequiredService<IQuestRepository>().CompleteQuestAsync(player.CharacterId, questId);
            await QuestPrizeHandler.GrantRewardsAsync(
                session, player, quest, QuestPrizeHandler.NoRewardChoice, services, Logger);
            Logger.LogInformation("{Name} quest {QuestId}: paid at the end of its timer (Reward_Auto)",
                player.Name, questId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name} quest {QuestId}: closing the timed objective failed", player.Name, questId);
        }
    }

 /// <summary>Fails a quest whose time limit has run out (2434 OnQuestFailed). The quest stays held until it is given up.</summary>
    private async Task ExpireTimeLimitAsync(ProudSession session, PlayerState player, uint questId, uint limitMs,
        TimeSpan delay, object token)
    {
        try
        {
            await Task.Delay(delay);
            if (!PlayerManager.TryGetPlayer(session.SessionId, out var current) || !ReferenceEquals(current, player))
                return;
            if (!FailTimers.TryGetValue(player, out var timers)
                || !timers.TryRemove(new KeyValuePair<uint, object>(questId, token)))
                return;
            if (!player.ActiveQuests.Contains(questId)) return;

            
            QuestFailState.MarkFailed(player, questId);
            var body = QuestFailPackets.BuildQuestFailed2434(questId, QuestFailPackets.FailedCode,
                QuestFailPackets.FailTypeTimeLimit, limitMs);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestFailed, writer => writer.WriteBytes(body));
            Logger.LogInformation("{Name} quest {QuestId}: time limit of {Ms} ms ran out, quest failed",
                player.Name, questId, limitMs);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name} quest {QuestId}: failing the timed-out quest failed", player.Name, questId);
        }
    }

 /// <summary>Active_Type of action rows that run when the quest is accepted.</summary>
    private const byte OnAcceptActive = 1;

 /// <summary>Active_Type of action rows that run when the quest's objective closes.</summary>
    private const byte OnObjectiveCloseActive = 3;

 /// <summary>Action_Type of a give-item action (hands over Action_Value2 of item Action_Value1).</summary>
    private const byte GiveItemAction = 3;

 /// <summary>Complete_Type of a deliver objective (Value1 = item, Value2 = count).</summary>
    private const byte DeliverObjective = 5;

 /// <summary>One row of the Quest_Info action columns, read once from the data table.</summary>
    internal sealed record QuestActionRow(uint Quest_ID, byte Active_Type, byte Action_Type, uint Action_Value1, uint Action_Value2);

    private static readonly Lazy<IReadOnlyDictionary<uint, QuestActionRow[]>?> ActionRows = new(LoadActionRows);

    private static IReadOnlyDictionary<uint, QuestActionRow[]>? LoadActionRows()
    {
        try
        {
            var path = Path.Combine("data", "Quest_Info.json");
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<QuestActionRow>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            return rows.GroupBy(r => r.Quest_ID).ToDictionary(g => g.Key, g => g.ToArray());
        }
        catch
        {
            return null; 
        }
    }

 /// <summary>
 /// Items the quest hands over when it is accepted: the Active_Type 1 give-item rows, in row order.
 /// </summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> AcceptGrants(QuestData quest)
    {
        if (ActionRows.Value is { } all)
            return all.TryGetValue(quest.Quest_ID, out var rows) ? SelectAcceptGrants(rows) : Array.Empty<(uint, int)>();
        return quest.Action_Type == GiveItemAction && quest.Action_Value1 != 0
            ? new[] { (quest.Action_Value1, (int)Math.Max(1u, quest.Action_Value2)) }
            : Array.Empty<(uint, int)>();
    }

 /// <summary>
 /// For a quest whose talk rows are tracked, stores the unmet talk state on the newly inserted quest document,
 /// so that a relog keeps the turn-in refused until the talk NPC is met.
 /// </summary>
    internal static async Task StoreTrackedRunStateAsync(IQuestRepository repository, PlayerState player, uint questId,
        GameDataStore store, ILogger logger)
    {
        if (store.QuestRows.TryGetValue(questId, out var rows) && QuestTalkProgress.TrackedRows(rows).Length > 0)
            await QuestProgressStore.SaveObjectiveStateAsync(repository, player, questId, store, logger);
    }

    internal static void BeginRun(PlayerState player, uint questId)
    {
        
        player.ActiveQuests.Add(questId);
        player.QuestKillProgress[questId] = 0;
        
        
        
        LootService.ResetExtraKillProgress(player, questId);
        QuestUseItemProgress.Reset(player, questId);
        
        
        
        QuestTalkProgress.Begin(player, questId);
        
        
        QuestFailState.Clear(player, questId);
        QuestFailTriggers.ResetKillCounts(player, questId);
    }

 /// <summary>Action_Type of a take-item action (takes Action_Value2 of item Action_Value1 from the bag).</summary>
    private const byte TakeItemAction = 4;

 /// <summary>Active_Type of action rows that run when the quest is completed (paid).</summary>
    private const byte OnCompleteActive = 2;

 /// <summary>
 /// Items the quest takes from the bag when it is accepted: the Active_Type 1 take-item rows. Empty if the table is unreadable.
 /// </summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> AcceptTakes(uint questId, GameDataStore store)
        => ActionRows.Value is { } all && all.TryGetValue(questId, out var rows)
            ? SelectTakeRows(rows, OnAcceptActive, store)
            : Array.Empty<(uint, int)>();

 /// <summary>
 /// Items the quest takes from the bag when it is paid: the Active_Type 2 take-item rows, excluding the quest's
 /// own objective item. Empty if the table is unreadable.
 /// </summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> CompletionTakes(uint questId, GameDataStore store)
    {
        if (ActionRows.Value is not { } all || !all.TryGetValue(questId, out var rows)) return Array.Empty<(uint, int)>();
        return SelectCompletionTakes(rows, store.QuestRows.TryGetValue(questId, out var objectives) ? objectives : null, store);
    }

 /// <summary>The completion takes among one quest's rows (see <see cref="CompletionTakes"/>).</summary>
    internal static (uint ItemId, int Count)[] SelectCompletionTakes(IEnumerable<QuestActionRow> rows,
        IEnumerable<QuestData>? objectiveRows, GameDataStore store)
    {
        var objectiveItems = objectiveRows is null
            ? new HashSet<uint>()
            : objectiveRows.Where(r => r.Complete_Type is CollectObjective or DeliverObjective)
                .Select(r => r.Complete_Type_Value1).ToHashSet();
        return SelectTakeRows(rows, OnCompleteActive, store).Where(t => !objectiveItems.Contains(t.ItemId)).ToArray();
    }

 /// <summary>The take rows of one Active_Type among a quest's rows (see <see cref="AcceptTakes"/>).</summary>
    internal static (uint ItemId, int Count)[] SelectTakeRows(IEnumerable<QuestActionRow> rows, byte activeType, GameDataStore store)
        => rows
            .Where(r => r.Active_Type == activeType && r.Action_Type == TakeItemAction && store.Items.ContainsKey(r.Action_Value1))
            .Select(r => (r.Action_Value1, (int)Math.Max(1u, r.Action_Value2)))
            .ToArray();

 /// <summary>The accept grants among one quest's Quest_Info rows (see <see cref="AcceptGrants"/>).</summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> SelectAcceptGrants(IEnumerable<QuestActionRow> rows)
        => SelectGiveRows(rows, OnAcceptActive);

 /// <summary>
 /// Items the quest hands over when its objective closes: the Active_Type 3 give-item rows, in row order.
 /// </summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> ObjectiveCloseGrants(uint questId)
        => ActionRows.Value is { } all && all.TryGetValue(questId, out var rows)
            ? SelectObjectiveCloseGrants(rows)
            : Array.Empty<(uint, int)>();

 /// <summary>The objective-close grants among one quest's Quest_Info rows (see <see cref="ObjectiveCloseGrants"/>).</summary>
    internal static IReadOnlyList<(uint ItemId, int Count)> SelectObjectiveCloseGrants(IEnumerable<QuestActionRow> rows)
        => SelectGiveRows(rows, OnObjectiveCloseActive);

    private static (uint ItemId, int Count)[] SelectGiveRows(IEnumerable<QuestActionRow> rows, byte activeType)
        => rows
            .Where(r => r.Active_Type == activeType && r.Action_Type == GiveItemAction && r.Action_Value1 != 0)
            .Select(r => (r.Action_Value1, (int)Math.Max(1u, r.Action_Value2)))
            .ToArray();

 /// <summary>
 /// Hands over the items the quest gives on accept. When such an item is a delivery objective of the quest, it is
 /// ticked and the quest is closed.
 /// </summary>
    private async Task GrantAcceptItemAsync(ProudSession session, PlayerState player, QuestData quest,
        GameDataStore store, IServiceProvider services)
    {
        var grants = AcceptGrants(quest);
        if (grants.Count == 0) return;

        var rows = store.QuestRows.TryGetValue(quest.Quest_ID, out var all) ? all : new[] { quest };
        var objectiveRows = rows.Count(r => r.Complete_Type != 0);
        var met = new bool[rows.Length];
        var firstRowTick = -1;
        var closedSent = false;
        var inventory = services.GetRequiredService<IInventoryRepository>();

        foreach (var (itemId, grantCount) in grants)
        {
            if (!store.Items.ContainsKey(itemId))
            {
                Logger.LogWarning("{Name} quest {QuestId}: accept item {Item} is not in ItemInfo, not granted",
                    player.Name, quest.Quest_ID, itemId);
                continue;
            }

            var granted = new InventoryItem
            {
                CharacterId = player.CharacterId,
                ItemId = itemId,
                Count = grantCount,
                Slot = -1,        
                StorageType = 0,  
            };
            try { await inventory.AddItemAsync(granted); }
            catch (Exception ex)
            {
                
                Logger.LogWarning(ex, "{Name} quest {QuestId}: saving accept item {Item} failed",
                    player.Name, quest.Quest_ID, itemId);
            }

            
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Complete_Type != DeliverObjective || row.Complete_Type_Value1 != itemId || met[i]) continue;

                var needed = row.Complete_Type_Value2 == 0 ? (byte)1 : (byte)Math.Min(row.Complete_Type_Value2, 255);
                met[i] = grantCount >= needed;
                if (i == 0) firstRowTick = Math.Min(grantCount, needed);

                var tickBody = QuestNotifyPackets.BuildQuestAddItem2424(
                    quest.Quest_ID, itemId, (ushort)Math.Min(grantCount, ushort.MaxValue));
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestAddItem, writer => writer.WriteBytes(tickBody));

                if (!closedSent && AllDeliverRowsMet(rows, met))
                {
                    closedSent = true;
                    var completeBody = QuestProgressPackets.BuildQuestComplete2427(quest.Quest_ID);
                    await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify, writer => writer.WriteBytes(completeBody));
                }
                break;
            }

            var record = WorldEntryService.BuildItemRecord126(granted);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                writer => writer.WriteBytes(LootNotifyPackets.BuildPickupItem2333(record)));

            Logger.LogInformation("{Name} quest {QuestId}: received accept item {Count}x {Item}",
                player.Name, quest.Quest_ID, grantCount, itemId);
        }

        if (firstRowTick >= 0 && (closedSent || objectiveRows == 1))
            player.QuestKillProgress[quest.Quest_ID] = (byte)firstRowTick;
    }

 /// <summary>True when every objective row is a deliver row that the accept grants have met, so the accept closes the quest.</summary>
    private static bool AllDeliverRowsMet(QuestData[] rows, bool[] met)
    {
        var any = false;
        for (int k = 0; k < rows.Length; k++)
        {
            if (rows[k].Complete_Type == 0) continue;
            if (rows[k].Complete_Type != DeliverObjective || !met[k]) return false;
            any = true;
        }
        return any;
    }
}
