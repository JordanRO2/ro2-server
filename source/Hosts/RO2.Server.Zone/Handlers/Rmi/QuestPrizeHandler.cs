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
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1378 (ReqQuestPrize): turns a quest in at its NPC. The server checks the objective,
/// completes the quest and pays its rewards.
/// </summary>
/// <remarks>
/// Request: giver NPC handle, quest id and, when the quest offers a choice, the chosen reward index.
/// The objective decides the answer. A kill or collect objective that is not met gets no answer and the
/// quest stays active. A talk objective met at another NPC is refused and the quest is kept. A completion
/// item missing from the bag is refused, and so is a quest that is not held.
/// Rewards are paid in this order: reward items, the quest prize, experience, then the balance update.
/// Some quests also grant a job change or a guild donation of the prize experience.
/// </remarks>
public class QuestPrizeHandler : ZoneRmiHandlerBase<QuestPrizeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestPrize;
    public override string Name => "QuestPrize";
    public QuestPrizeHandler(IServiceScopeFactory s, ILogger<QuestPrizeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Reward index meaning that no reward was chosen.</summary>
    internal const uint NoRewardChoice = 0xFFFFFFFF;

    // Request body (compact-length envelope): body+0 marker, body+4 msgTag 58302, body+6 the sender's PC handle,
    // body+10 u32 quest-giver NPC id, body+14 u32 quest id, body+18 u32 reward choice (absent when the body is 18 bytes).
    // The quest id sits at body+14 here, the opposite order from ReqQuestAccept (1374), where it is at body+10.

    /// <summary>Offset of the quest-giver id; the envelope marker, msgTag and PC handle come before it.</summary>
    private const int RequestGiverOffset = 10;

    /// <summary>Offset of the reward choice, which follows the quest id.</summary>
    private const int RequestRewardChoiceOffset = 18;

    /// <summary>Shortest body that holds the giver id and the quest id.</summary>
    private const int RequestMinBytes = RequestRewardChoiceOffset;

    /// <summary>Body length that also holds the reward choice.</summary>
    private const int RequestFullBytes = RequestRewardChoiceOffset + sizeof(uint);

    /// <summary>Quest_Info <c>Complete_Type</c> of a place objective (closed by the position check of ReqQuestComplete 1377).</summary>
    private const byte PlaceObjective = 3;

    /// <summary>Quest_Info <c>Complete_Type</c> of a timed objective (closed when its time runs out).</summary>
    private const byte TimedObjective = 34;

    /// <summary>Quest_Info <c>Reward_Type</c> that pays every filled reward slot.</summary>
    private const byte RewardTypeAllSlots = 1;

    /// <summary>Quest_Info <c>Reward_Type</c> that pays the one slot named by the reward choice.</summary>
    private const byte RewardTypePickOne = 2;

    /// <summary>Quest_Info <c>Reward_Type</c> that pays the one slot named by the reward choice, like <see cref="RewardTypePickOne"/>.</summary>
    private const byte RewardTypePickOneAlt = 5;

    /// <summary>Value of the session quest counter once an objective is closed and reported to the client (1 for
    /// talk, place, use-item and timed quests).</summary>
    private const ushort ObjectiveClosedCounter = 1;

    /// <summary><c>Complete_Type_Value3</c> of a talk row that is met by speaking to its NPC. A row with Value3 1 is
    /// closed by the client itself with a 1377.</summary>
    private const uint TalkRowMetByDialog = 0;

    /// <summary><c>InventoryItem.StorageType</c> of the main bag.</summary>
    private const byte StorageBag = 0;

    /// <summary><c>InventoryItem.Slot</c> that lets the inventory pick the first free bag slot.</summary>
    private const short AutoSlot = -1;

    /// <summary>Offset of the stack count byte in a 126-byte item record. 0 empties the client's cell.</summary>
    private const int ItemRecordCountOffset = 14;

    /// <summary>How many times the OnPcInitTrait / OnPcTrait pair is sent after a job change.</summary>
    private const int JobChangeTraitPasses = 2;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The envelope header and the sender's PC handle are skipped; see the request layout above.
        if (payload.Length < RequestMinBytes) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(RequestGiverOffset);
        var giverId = reader.ReadUInt32();
        var questId = reader.ReadUInt32();
        var rewardChoice = payload.Length >= RequestFullBytes ? reader.ReadUInt32() : NoRewardChoice;

        var store = services.GetRequiredService<GameDataStore>();
        if (!store.Quests.TryGetValue(questId, out var quest))
        {
            Logger.LogWarning("{Name} claimed reward for unknown quest {QuestId}", player.Name, questId);
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorInsertRejected)));
            return;
        }

        
        
        
        if (QuestFailState.IsFailed(player, questId) && player.ActiveQuests.Contains(questId))
        {
            Logger.LogInformation("{Name} turned in quest {QuestId}, which failed its time limit; ignored",
                player.Name, questId);
            return;
        }

        
        
        if (!player.ActiveQuests.Remove(questId))
        {
            
            
            if (!PaidThisLogin(player, questId))
            {
                Logger.LogDebug("{Name} claimed reward for quest {QuestId}, which it does not hold; ignored",
                    player.Name, questId);
                return;
            }
            Logger.LogWarning("{Name} claimed reward again for quest {QuestId}, already paid", player.Name, questId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorPrizeRejected)));
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        bool met;
        IReadOnlyList<InventoryItem>? bag;
        try { (met, bag) = await CheckObjectiveAsync(player, quest, store, inventory); }
        catch
        {
            player.ActiveQuests.Add(questId);
            throw;
        }
        if (!met)
        {
            
            
            player.ActiveQuests.Add(questId);
            Logger.LogInformation("{Name} turned in quest {QuestId} with its objective unmet (Complete_Type {Type}); ignored",
                player.Name, questId, quest.Complete_Type);
            return;
        }

        player.QuestKillProgress.TryGetValue(questId, out var progress);
        
        var killClosed = LootService.KillObjectiveClosed(player, questId);

        
        
        
        
        if (TalkMetElsewhereUnmet(quest, giverId, ObjectiveClosedThisSession(quest, progress, killClosed),
                QuestTalkProgress.AcceptedThisLogin(player, questId) || QuestProgressStore.PersistedStateKnown(player, questId),
                store))
        {
            player.ActiveQuests.Add(questId);
            Logger.LogInformation("{Name} turned in quest {QuestId} at {Giver} before meeting its talk NPC {Npc}; refused (21712)",
                player.Name, questId, giverId, quest.Complete_Type_Value1);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorRequirementUnmet)));
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                    questId, QuestProgressPackets.QuestErrorPrizeNotGiven)));
            return;
        }

        
        
        
        
        
        var completionTakes = QuestAcceptHandler.CompletionTakes(questId, store);
        if (completionTakes.Count > 0)
        {
            bag ??= await inventory.GetByCharacterAsync(player.CharacterId);
            if (completionTakes.Any(t => CountInBag(bag, t.ItemId) < t.Count))
            {
                player.ActiveQuests.Add(questId);
                Logger.LogInformation("{Name} turned in quest {QuestId} without the item it takes; refused (21708)",
                    player.Name, questId);
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        questId, QuestProgressPackets.QuestErrorPrizeNotGiven)));
                return;
            }
        }

        Logger.LogInformation("{Name} turned in quest {QuestId} at {Giver}", player.Name, questId, giverId);

        
        
        
        var sideClosed = SideClosedBy(player, questId, giverId, store);
        foreach (var other in sideClosed) player.QuestKillProgress[other] = ObjectiveClosedCounter;
        foreach (var other in sideClosed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestUpdateNotify,
                writer => writer.WriteBytes(QuestNotifyPackets.BuildQuestUpdateNotify2432(other, giverId)));

        
        
        
        
        
        
        
        
        if (quest.Complete_Type == QuestProgressStore.TalkObjective && !ObjectiveClosedThisSession(quest, progress, killClosed))
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestUpdateNotify,
                writer => writer.WriteBytes(QuestNotifyPackets.BuildQuestUpdateNotify2432(questId, giverId)));
        }
        foreach (var other in sideClosed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(other)));
        var closesNow = !ObjectiveClosedThisSession(quest, progress, killClosed);
        if (closesNow)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));
        }

        
        
        
        
        
        var cellsNeeded = RewardItemsOf(quest, rewardChoice).Count(r => r.Id != 0 && store.Items.ContainsKey(r.Id))
            + (closesNow ? QuestAcceptHandler.ObjectiveCloseGrants(questId).Count(g => store.Items.ContainsKey(g.ItemId)) : 0)
            + sideClosed.Sum(other => QuestAcceptHandler.ObjectiveCloseGrants(other).Count(g => store.Items.ContainsKey(g.ItemId)));
        if (cellsNeeded > 0)
        {
            bag ??= await inventory.GetByCharacterAsync(player.CharacterId);
            var emptied = quest.Complete_Type is QuestProgressStore.CollectObjective or QuestProgressStore.DeliverObjective && quest.Complete_Type_Value1 != 0
                ? StacksEmptiedBy(bag, quest.Complete_Type_Value1, ObjectiveItemCount(quest))
                : new HashSet<ulong>();
            if (BagCapacity.FreeCells(bag.Where(i => !emptied.Contains(i.Id)), store.Items).Count < cellsNeeded)
            {
                player.ActiveQuests.Add(questId);
                
                
                
                if (closesNow && QuestAcceptHandler.ObjectiveCloseGrants(questId).Count == 0 && quest.Complete_Type != QuestProgressStore.KillObjective)
                    player.QuestKillProgress[questId] = (quest.Complete_Type is QuestProgressStore.CollectObjective or QuestProgressStore.DeliverObjective) ? ObjectiveTotal(quest) : ObjectiveClosedCounter;
                
                
                foreach (var other in sideClosed)
                    if (QuestAcceptHandler.ObjectiveCloseGrants(other).Count > 0)
                        player.QuestKillProgress.Remove(other);
                
                var keptRepo = services.GetRequiredService<IQuestRepository>();
                await QuestProgressStore.SaveObjectiveStateAsync(keptRepo, player, questId, store, Logger);
                foreach (var other in sideClosed)
                    await QuestProgressStore.SaveObjectiveStateAsync(keptRepo, player, other, store, Logger);
                Logger.LogInformation("{Name} turned in quest {QuestId}, but its {Cells} reward item(s) do not fit the bag; refused",
                    player.Name, questId, cellsNeeded);
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        questId, QuestProgressPackets.QuestErrorBagFull)));
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestError,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestError2431(
                        questId, QuestProgressPackets.QuestErrorPrizeNotGiven)));
                return;
            }
        }

        player.QuestKillProgress.Remove(questId);
        LootService.ResetExtraKillProgress(player, questId);
        QuestUseItemProgress.Reset(player, questId);

        
        
        if (sideClosed.Count > 0)
        {
            var sideRepo = services.GetRequiredService<IQuestRepository>();
            foreach (var other in sideClosed)
                await QuestProgressStore.SaveObjectiveStateAsync(sideRepo, player, other, store, Logger);
        }

        
        
        foreach (var other in sideClosed)
            await GrantObjectiveCloseItemsAsync(session, player, other, store, inventory, Logger);

        if (closesNow)
        {
            
            
            
            
            
            await GrantObjectiveCloseItemsAsync(session, player, questId, store, inventory, Logger);
        }

        
        
        if (quest.Complete_Type is QuestProgressStore.CollectObjective or QuestProgressStore.DeliverObjective && quest.Complete_Type_Value1 != 0 && bag is not null)
            await RemoveFromBagAsync(session, player, inventory, bag, quest.Complete_Type_Value1,
                ObjectiveItemCount(quest), Logger, services);

        var questRepo = services.GetRequiredService<IQuestRepository>();
        await questRepo.CompleteQuestAsync(player.CharacterId, questId);

        await GrantRewardsAsync(session, player, quest, rewardChoice, services, Logger);

        
        
        
        if (completionTakes.Count > 0)
        {
            var paidBag = await inventory.GetByCharacterAsync(player.CharacterId);
            foreach (var (itemId, count) in completionTakes)
                await RemoveFromBagAsync(session, player, inventory, paidBag, itemId, count, Logger, services);
        }
    }

 /// <summary>
 /// Returns the ids of the bag stacks that a hand-over of <paramref name="count"/> of <paramref name="itemId"/>
 /// empties, so the bag check can count those cells as free.
 /// </summary>
    internal static HashSet<ulong> StacksEmptiedBy(IReadOnlyList<InventoryItem> bag, uint itemId, int count)
    {
        var emptied = new HashSet<ulong>();
        var left = count;
        foreach (var stack in bag)
        {
            if (left <= 0) break;
            if (stack.StorageType != StorageBag || stack.ItemId != itemId) continue;
            var held = Math.Max(1, stack.Count);
            if (held <= left) emptied.Add(stack.Id);
            left -= held;
        }
        return emptied;
    }

 /// <summary>
 /// Returns the reward items of a quest for the given reward choice, by reward type. Empty slots are
 /// included and must be skipped by the caller.
 /// </summary>
    internal static (uint Id, uint Count)[] RewardItemsOf(QuestData quest, uint rewardChoice)
    {
        var slots = quest.RewardItemSlots();
        return quest.Reward_Type switch
        {
            RewardTypeAllSlots => slots,
            RewardTypePickOne or RewardTypePickOneAlt => rewardChoice < (uint)slots.Length ? new[] { slots[rewardChoice] } : Array.Empty<(uint Id, uint Count)>(),
            _ => Array.Empty<(uint Id, uint Count)>(),
        };
    }

 /// <summary>
 /// Checks whether the objective of <paramref name="quest"/> is met for the objectives this server tracks.
 /// For item objectives, also returns the bag that was counted, so that the caller removes from the same list.
 /// </summary>
    internal static async Task<(bool Met, IReadOnlyList<InventoryItem>? Bag)> CheckObjectiveAsync(
        PlayerState player, QuestData quest, GameDataStore store, IInventoryRepository inventory)
    {
        switch (quest.Complete_Type)
        {
            case QuestProgressStore.KillObjective when quest.Complete_Type_Value1 != 0:
                player.QuestKillProgress.TryGetValue(quest.Quest_ID, out var kills);
                if (kills < ObjectiveTotal(quest)) return (false, null);
                if (store.QuestRows.TryGetValue(quest.Quest_ID, out var rows))
                {
                    for (int i = 1; i < rows.Length; i++)
                    {
                        var row = rows[i];
                        if (row.Complete_Type != QuestProgressStore.KillObjective || row.Complete_Type_Value1 == 0) continue;
                        if (LootService.ExtraKillCount(player, quest.Quest_ID, row.Complete_Type_Value1) < ObjectiveTotal(row))
                            return (false, null);
                    }
                }
                return (true, null);
            case QuestProgressStore.CollectObjective or QuestProgressStore.DeliverObjective when quest.Complete_Type_Value1 != 0:
                var bag = await inventory.GetByCharacterAsync(player.CharacterId);
                return (CountInBag(bag, quest.Complete_Type_Value1) >= ObjectiveItemCount(quest), bag);
            default:
                return (true, null);
        }
    }

 /// <summary>
 /// Returns the objective total as the progress counter stores it, capped at 65,535.
 /// </summary>
    private static ushort ObjectiveTotal(QuestData quest) => QuestProgressStore.NeededKills(quest);

 /// <summary>Returns how many objective items a collect or deliver turn-in takes.</summary>
    internal static int ObjectiveItemCount(QuestData quest) => (int)Math.Max(1u, quest.Complete_Type_Value2);

 /// <summary>
 /// Returns whether this session has already sent the objective-closed notification for the quest. Quests
 /// whose objective closes before the turn-in are only treated as closed when that notification was sent.
 /// </summary>
    private static bool ObjectiveClosedThisSession(QuestData quest, ushort progress, bool killClosed) => quest.Complete_Type switch
    {
        QuestProgressStore.KillObjective => killClosed,
        QuestProgressStore.CollectObjective or QuestProgressStore.DeliverObjective => progress >= ObjectiveTotal(quest),
        QuestProgressStore.TalkObjective or PlaceObjective or QuestProgressStore.UseItemObjective or TimedObjective
            => progress >= ObjectiveClosedCounter,
        _ => false,
    };

 /// <summary>
 /// Returns whether a turn-in must be refused because the quest starts with a talk objective at another
 /// NPC that the player has not met in this login. See <see cref="TalkMetElsewhereUnmet"/>.
 /// </summary>
    internal static bool TalkMetElsewhereUnmet(QuestData quest, uint giverId, bool objectiveClosed, bool acceptedThisLogin,
        GameDataStore store) =>
        quest.Complete_Type == QuestTalkProgress.TalkObjective && quest.Complete_Type_Value1 != 0
        && quest.Complete_Type_Value1 != quest.Reward_NPC_ID && quest.Complete_Type_Value1 != giverId
        && !objectiveClosed && acceptedThisLogin && QuestTalkProgress.IsMeetableNpc(store, quest.Complete_Type_Value1);

 /// <summary>
 /// Returns the held quests that the turn-in of <paramref name="questId"/> at <paramref name="giverId"/>
 /// also closes: single-talk quests whose talk NPC is the giver and that are not yet closed or failed.
 /// </summary>
    internal static List<uint> SideClosedBy(PlayerState player, uint questId, uint giverId, GameDataStore store)
    {
        var closed = new List<uint>();
        if (giverId == 0) return closed;
        foreach (var other in player.ActiveQuests.ToArray())
        {
            if (other == questId || !store.QuestRows.TryGetValue(other, out var rows)) continue;
            if (!IsSingleTalkRow(rows) || rows[0].Complete_Type_Value1 != giverId) continue;
            if (player.QuestKillProgress.TryGetValue(other, out var p) && p != 0) continue;
            if (QuestFailState.IsFailed(player, other)) continue;
            closed.Add(other);
        }
        closed.Sort();
        return closed;
    }

 /// <summary>Returns whether the quest's only objective is its first row, a talk objective with an NPC.</summary>
    internal static bool IsSingleTalkRow(QuestData[] rows) =>
        rows.Length > 0 && rows[0].Complete_Type == QuestTalkProgress.TalkObjective && rows[0].Complete_Type_Value3 == TalkRowMetByDialog
        && rows[0].Complete_Type_Value1 != 0 && rows.Skip(1).All(r => r.Complete_Type == QuestProgressStore.NoObjective);

 /// <summary>
 /// Quests paid to the player in this login. A later turn-in of one of them is refused, as the client expects.
 /// Scoped to one login.
 /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerState,
        System.Collections.Concurrent.ConcurrentDictionary<uint, byte>> PaidQuests = new();

    private static bool PaidThisLogin(PlayerState player, uint questId)
        => PaidQuests.TryGetValue(player, out var paid) && paid.ContainsKey(questId);

 /// <summary>Returns how many of <paramref name="itemId"/> the bag holds. A stack without a count counts as one item.</summary>
    internal static long CountInBag(IReadOnlyList<InventoryItem> bag, uint itemId) =>
        bag.Where(i => i.StorageType == StorageBag && i.ItemId == itemId).Sum(i => (long)Math.Max(1, i.Count));

 /// <summary>
 /// Removes up to <paramref name="count"/> of <paramref name="itemId"/> from the bag, stack by stack, and
 /// sends one update per stack touched. Returns how many were removed.
 /// </summary>
    internal static async Task<int> RemoveFromBagAsync(ProudSession session, PlayerState player,
        IInventoryRepository inventory, IReadOnlyList<InventoryItem> bag, uint itemId, int count, ILogger logger,
        IServiceProvider? services = null)
    {
        var removed = 0;
        foreach (var stack in bag)
        {
            if (removed >= count) break;
            if (stack.StorageType != StorageBag || stack.ItemId != itemId) continue;

            var held = Math.Max(1, stack.Count);
            var take = Math.Min(count - removed, held);
            var left = held - take;

            
            
            var record = ItemPackets.BuildItemRecord126(stack);
            record[ItemRecordCountOffset] = (byte)Math.Min(left, byte.MaxValue);

            if (left == 0) await inventory.RemoveItemAsync(stack.Id);
            else { stack.Count = left; await inventory.UpdateItemAsync(stack); }

            if (services is not null)
                await KharaService.OnHeldItemChangedAsync(session, player, itemId, services, logger);

            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
                writer => writer.WriteBytes(QuestProgressPackets.BuildItemRemoved2379(player.EntityId, record)));
            removed += take;
        }

        if (removed > 0)
            logger.LogInformation("{Name} handed over {Count}x item {Item}", player.Name, removed, itemId);
        return removed;
    }

 /// <summary>
 /// Hands over the items that the quest gives when its objective closes. Each item is saved to the bag and
 /// shown to the client. Call it right after the objective-closed notification, on the same path only.
 /// </summary>
    internal static async Task GrantObjectiveCloseItemsAsync(ProudSession session, PlayerState player, uint questId,
        GameDataStore store, IInventoryRepository inventory, ILogger logger)
    {
        foreach (var (itemId, count) in QuestAcceptHandler.ObjectiveCloseGrants(questId))
        {
            if (!store.Items.ContainsKey(itemId))
            {
                logger.LogWarning("{Name} quest {QuestId}: objective-close item {Item} is not in ItemInfo, not granted",
                    player.Name, questId, itemId);
                continue;
            }

            var granted = new InventoryItem
            {
                CharacterId = player.CharacterId,
                ItemId = itemId,
                Count = count,
                Slot = AutoSlot,
                StorageType = StorageBag,
            };
            try { await inventory.AddItemAsync(granted); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Name} quest {QuestId}: saving objective-close item {Item} failed",
                    player.Name, questId, itemId);
            }

            var record = ItemPackets.BuildItemRecord126(granted);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                writer => writer.WriteBytes(LootNotifyPackets.BuildPickupItem2333(record)));
            logger.LogInformation("{Name} quest {QuestId}: received objective-close item {Count}x {Item}",
                player.Name, questId, count, itemId);
        }
    }

 /// <summary>
 /// Pays the rewards of a completed quest in this order: reward items, the quest prize, experience and
 /// the balance update. Shared by the turn-in and the completion of quests.
 /// </summary>
    internal static async Task GrantRewardsAsync(ProudSession session, PlayerState player, QuestData quest,
        uint rewardChoice, IServiceProvider services, ILogger logger)
    {
        var store = services.GetRequiredService<GameDataStore>();
        
        PaidQuests.GetValue(player, _ => new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>())
            .TryAdd(quest.Quest_ID, 0);

        // Which items are paid depends on Reward_Type, not on the wire: type 1 pays every filled slot whatever the
        // reward choice says; types 2 and 5 pay only the slot at the 0-based index of the choice (slot i is
        // Reward_Item_ID(i+1)); any other type pays none. A choice row with no pick, or a pick past the slots or of an
        // empty slot, pays no item.
        var slots = quest.RewardItemSlots();
        var granted = RewardItemsOf(quest, rewardChoice);
        if (quest.Reward_Type is RewardTypePickOne or RewardTypePickOneAlt && (rewardChoice >= (uint)slots.Length || slots[rewardChoice].Id == 0))
            logger.LogInformation("{Name} quest {QuestId}: choice reward with no valid pick ({Choice:X8}), no item paid",
                player.Name, quest.Quest_ID, rewardChoice);

        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var paidItemIds = new List<uint>(granted.Length);
        foreach (var (rewardItemId, rewardCount) in granted)
        {
            if (rewardItemId == 0 || !store.Items.ContainsKey(rewardItemId)) continue;
            paidItemIds.Add(rewardItemId);
            var count = (int)Math.Max(1, rewardCount);
            var rewardItem = new InventoryItem
            {
                CharacterId = player.CharacterId,
                ItemId = rewardItemId,
                Count = count,
                Slot = AutoSlot,
                StorageType = StorageBag,
            };
            try { await inventory.AddItemAsync(rewardItem); }
            catch {   }

            
            
            
            await KharaService.OnHeldItemChangedAsync(session, player, rewardItemId, services, logger);

            var rewardRecord = ItemPackets.BuildItemRecord126(rewardItem);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                writer => writer.WriteBytes(LootNotifyPackets.BuildPickupItem2333(rewardRecord)));
            logger.LogInformation("{Name} received quest reward {Count}x item {Item}",
                player.Name, count, rewardItemId);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var expRate = services.GetRequiredService<EffectService>().ExpRateOf(player);
        var exp = EffectService.ApplyExpRate(quest.Reward_EXP, expRate);
        if (expRate != 0)
            logger.LogDebug("{Name} quest {QuestId}: Reward_EXP {Base} at EXP rate {Rate}/10000 -> {Exp}",
                player.Name, quest.Quest_ID, quest.Reward_EXP, expRate, exp);
        var money = (uint)Math.Max(0, quest.Reward_Money);

        
        
        
        
        
        
        if (QuestInfoExtraColumns.First(quest.Quest_ID) is { Quest_Frequency: > 0 } repeatRow)
        {
            var completedIds = await services.GetRequiredService<IQuestRepository>()
                .GetCompletedQuestIdsAsync(player.CharacterId);
            var completions = Math.Clamp(completedIds.Count(id => id == quest.Quest_ID), 1, (int)repeatRow.Quest_Frequency);
            var reActBody = QuestReActPackets.BuildUpdateQuestReAct2784(player.CharacterUid, quest.Quest_ID,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (byte)completions);
            await session.SendRmiAsync(ZoneS2COpcodes.NtyUpdateQuestReAct, writer => writer.WriteBytes(reActBody));
        }

        await session.SendRmiAsync(ZoneS2COpcodes.OnQuestPrize,
            writer => writer.WriteBytes(QuestProgressPackets.BuildQuestPrize2429(quest.Quest_ID, exp, money)));

        
        
        
        var donation = await DonateToGuildAsync(session, player, exp, services);
        ulong kept = exp - donation.Donated;

        
        
        
        
        var levelBefore = player.Level;
        if (kept > 0)
        {
            var combat = services.GetRequiredService<CombatService>();
            await combat.GrantExpAsync(player, kept);
        }

        
        
        await QuestLevelGrants.GrantAsync(session, player, levelBefore, services, logger);

        
        
        var zenyBody = ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, money);
        await session.SendRmiAsync(ZoneS2COpcodes.OnZenyChange,
            writer => writer.WriteBytes(zenyBody));

        
        
        
        
        
        
        
        
        
        
        
        if (donation.LevelledGuild is { } levelledGuild)
        {
            await GuildPackets.BroadcastToGuildAsync(levelledGuild, services.GetRequiredService<PlayerStateManager>(),
                services.GetRequiredService<BroadcastService>(), ZoneS2COpcodes.NfyGuildLevelUP,
                GuildBribePackets.BuildNfyGuildLevelUp(donation.NewGuildLevel));
        }

        
        
        
        
        
        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, paidItemIds, services, logger);

        
        
        
        
        
        
        
        await KharaService.OnQuestCompletedAsync(session, player, quest.Quest_ID, services, logger);

        await ApplyQuestJobChangeAsync(session, player, quest, services, logger);
    }

 /// <summary>
 /// Donates part of the quest prize experience to the player's guild, at the player's donation rate.
 /// Returns the amount donated and any guild level change.
 /// </summary>
    internal static async Task<GuildDonation> DonateToGuildAsync(ProudSession session, PlayerState player,
        ulong prizeExp, IServiceProvider services)
    {
        if (prizeExp == 0) return default;
        
        
        if (GuildService.Instance.GetByCharId(player.CharacterId) is not { } guild) return default;
        var rate = GuildService.BribePercentOf(guild, player.CharacterId);
        if (rate == 0) return default;

        var bribe = GuildBribePackets.HuntingBribeAmount(prizeExp, rate);
        if (bribe == 0) return default;

        await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildBribeHunting,
            writer => writer.WriteBytes(GuildBribePackets.BuildNfyGuildBribeHunting(bribe)));

        var store = services.GetRequiredService<GameDataStore>();
        var (before, after) = await GuildService.Instance.CreditMemberExpAsync(guild, player.CharacterId, bribe);
        var oldLevel = GuildRosterPackets.GuildLevelOf(before, store.GuildExpTable).Level;
        var newLevel = GuildRosterPackets.GuildLevelOf(after, store.GuildExpTable).Level;
        return newLevel > oldLevel
            ? new GuildDonation(bribe, guild, newLevel)
            : new GuildDonation(bribe, null, 0);
    }

 /// <summary>Result of <see cref="DonateToGuildAsync"/>: the experience donated and any guild level change.</summary>
    internal readonly record struct GuildDonation(ulong Donated, Guild? LevelledGuild, byte NewGuildLevel);

 /// <summary>Quest_Info action type of a job change granted by a quest.</summary>
    private const byte JobChangeAction = 19;

 /// <summary>
 /// Applies the job change that a quest pays. The skills are reset to the new job's starters, the job is
 /// set and saved, the stat sheet is recomputed and the client is notified.
 /// </summary>
    private static async Task ApplyQuestJobChangeAsync(ProudSession session, PlayerState player, QuestData quest,
        IServiceProvider services, ILogger logger)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var rows = store.QuestRows.TryGetValue(quest.Quest_ID, out var all) ? all : new[] { quest };
        var jobRow = rows.FirstOrDefault(r => r.Action_Type == JobChangeAction && r.Action_Value1 != 0);
        if (jobRow is null) return;
        var jobId = jobRow.Action_Value1;
        var change = jobId > byte.MaxValue ? null
            : store.JobChangeTable.FirstOrDefault(j => j.Job_ID == jobId && j.Need_Quest_ID == quest.Quest_ID)
              ?? store.JobChangeTable.FirstOrDefault(j => j.Job_ID == jobId && j.ISJobMaster == 0)
              ?? store.JobChangeTable.FirstOrDefault(j => j.Job_ID == jobId);
        if (change is null)
        {
            logger.LogWarning("{Name} quest {QuestId}: job change to {Job}, which is no JobChange_Table Job_ID; not applied",
                player.Name, quest.Quest_ID, jobId);
            return;
        }

        var newJob = (byte)jobId;
        var oldJob = player.Job;
        var statReset = false;

        
        
        
        byte? talentPool = null;
        if (change.Talent_Reset != 0)
        {
            try { talentPool = await ItemUseHandler.ResetSkillsAsync(session, player, newJob, store, services, logger); }
            catch (System.Exception ex) { logger.LogWarning(ex, "{Name}: the job-change skill reset failed", player.Name); }
        }

        if (player.Character is { } c)
        {
            c.Job = newJob;
            
            
            if (player.Level != 0 && c.Level != player.Level) c.Level = player.Level;
            
            
            
            statReset = change.Stat_Reset != 0 && StatService.ResetBonusStats(store, c);
            if (statReset)
            {
                player.Str = c.Str; player.Agi = c.Agi; player.Int = c.Int;
                player.Wis = c.Wis; player.Vit = c.Vit; player.Luk = c.Luk;
                try { await services.GetRequiredService<ICharacterRepository>().SaveCharacterAsync(c); }
                catch (System.Exception ex) { logger.LogWarning(ex, "{Name}: could not persist the job-change stat reset", player.Name); }
            }
            
            
            
            
            var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(c.Id);
            var trait = services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, c, equipped);
            var initTrait = TraitPackets.BuildPcInitTrait2595(c, trait);
            
            
            
            for (int pass = 0; pass < JobChangeTraitPasses; pass++)
            {
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait, initTrait);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, trait);
            }
        }
        
        
        player.Job = newJob;

        
        
        
        var notifyBody = JobChangePackets.BuildSkillTranslateNotify2481(player.EntityId, newJob);
        await session.SendRmiAsync(ZoneS2COpcodes.OnSkillTranslateNotify, writer => writer.WriteBytes(notifyBody));
        await services.GetRequiredService<BroadcastService>().BroadcastToNearbyAsync(
            player, ZoneS2COpcodes.OnSkillTranslateNotify, writer => writer.WriteBytes(notifyBody));

        var translateBody = JobChangePackets.BuildSkillTranslate2480(player.EntityId, newJob);
        await session.SendRmiAsync(ZoneS2COpcodes.OnSkillTranslate, writer => writer.WriteBytes(translateBody));

        logger.LogInformation("{Name} quest {QuestId}: job changed {Old} -> {New}{Reset}{Skills}",
            player.Name, quest.Quest_ID, oldJob, newJob, statReset ? ", bonus stat points refunded" : "",
            talentPool is { } p ? $", skills reset ({p} talent points)" : "");
    }
}
