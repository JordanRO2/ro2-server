using RO2.Economy.Domain;
using RO2.Skills.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1323 ReqPCItemUse (21 bytes, magic 0xE2A5)</summary>
public class ItemUseHandler : ZoneRmiHandlerBase<ItemUseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemUse;
    public override string Name => "ItemUse";
    public ItemUseHandler(IServiceScopeFactory s, ILogger<ItemUseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>ItemInfo Item_Type of the repair kits (12 rows named "수리키트", e.g. 60000208, 70000003).</summary>
    private const byte RepairKitItemType = 27;

    /// <summary>ItemInfo Item_Type of the remote-warehouse scrolls ("원격 창고스크롤": 30000280, 60000485, 70000074).</summary>
    private const byte RemoteBankScrollItemType = 43;

    /// <summary>ItemInfo Item_Type of cards. They carry Durability 10000, but a kit does not repair them.</summary>
    private const byte CardItemType = 13;

    /// <summary>ItemInfo Item_Type of the skill-reset scrolls ("스킬 초기화 주문서", 18 rows, e.g. 60000060, 21330012).</summary>
    private const byte SkillResetScrollItemType = 26;

    /// <summary>
    /// CharacterQuickSlot.EntryType of a skill on the bar: the type-2 entries of the 2472 carry skill ids
    /// (group*100+level, e.g. 410101) and no item uid, while the type-1 entries carry item template ids and uids.
    /// </summary>
    private const byte SkillQuickSlotEntryType = 2;

    /// <summary>Quest_Info Complete_Type of a "use item" objective (Value2 = the item, Value3 = how many uses).</summary>
    private const byte UseItemObjective = 6;

    /// <summary>ItemInfo Item_Type of the stat-reset scrolls ("능력치 초기화 주문서"): they refund every allocated bonus stat point.</summary>
    private const byte StatResetScrollItemType = 25;

    /// <summary>Quest_Info Complete_Type 0: the row carries no objective.</summary>
    private const byte NoObjective = 0;

    /// <summary>
    /// Value stored in <see cref="PlayerState.QuestKillProgress"/> for a use-item quest once its objective closed:
    /// "the objective's 2427 went out this session", read by the turn-in to skip a second 2427.
    /// </summary>
    private const ushort ObjectiveClosedMark = 1;

    /// <summary>Most uses a type-6 objective row can ask for (the use counter is a byte).</summary>
    private const uint MaxUsesPerRow = byte.MaxValue;

    /// <summary>msgTag of 1323 (58021 = 0xE2A5), checked by <see cref="InventoryRequest.Validate"/>.</summary>
    private const ushort MsgTag = 58021;

    // C2S 1323 blob layout: [u16 selfLen][u16 msgTag][u32 playerHandle][u64 itemUid][u32 targetHandle][u8 useSource].
    private const int RequestBlobLength = 21;
    private const int MsgTagOffset = 2;
    private const int PlayerHandleOffset = 4;
    private const int ItemUidOffset = 8;
    private const int UseSourceOffset = 20;

    /// <summary>Client container of the main bag, the only container a 1323 can name (the request carries none).</summary>
    private const byte MainBagContainer = 1;

    // Client (bag, slot) cell of a flat bag slot, the same mapping as WriteItemInstance. Flat slots 0-19
    // (BagCapacity.MainBagCells) are the main bag, shown as bag 1 cells 50-69; each extra bag then adds
    // BagCapacity.ExtraBagStride flat slots and is shown as bag 2..4 cells 0-34.
    private const int MainBagSlotBase = 50;
    private const int FirstExtraClientBag = 2;
    private const int LastExtraClientBag = FirstExtraClientBag + BagCapacity.ExtraBagCount - 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // C2S 1323 ReqPCItemUse: 21-byte blob, msgTag 58021, shaped
        //   [u16 selfLen][u16 msgTag][u32 playerHandle][u64 itemUid@8][u32 targetHandle@0x10]
        //   [u8 useSource@0x14]
        // The item uid is a 64-bit value and is read whole.
        // Lenient: the declared length is not compared, only that the blob reaches useSource.
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < RequestBlobLength) return;

        var msgTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(MsgTagOffset));
        var clientHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(PlayerHandleOffset));
        var itemUid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(ItemUidOffset));
        var useSource = blob[UseSourceOffset];

        Logger.LogDebug("{Name} uses item uid {Uid} (source {Source})",
            player.Name, itemUid, useSource);

        // INV-H12: the tag and the handle must be this player's (main bag container / slot 0 / qty 1: the request carries none).
        // The handle echoed into the 2329 / 2345 below is player.EntityId, never the client's byte. This is
        // defensive: a forged tag or handle is refused silently.
        if (InventoryRequest.Validate(msgTag, MsgTag, clientHandle, player.EntityId,
                container: MainBagContainer, slot: 0, qty: 1, stackCount: 1) != InvReject.None)
        {
            Logger.LogInformation("{Name} sent a 1323 with a bad tag/handle (tag {Tag}, handle 0x{Handle:X8}); refused",
                player.Name, msgTag, clientHandle);
            return;
        }
        var actorHandle = player.EntityId;

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var used = await inventory.GetByIdAsync(itemUid);
        // Ownership guard: uids are global; only the owner may consume the item (else a client could
        // burn another player's item by uid).
        if (used is null || used.CharacterId != player.CharacterId) return;

        // INV-H7 pre-gate: nothing below (effects, side effects, consumption, 2329) runs for a use that is not legal.
        // The order of the effects and of the 2329 after this point is unchanged. Every rule here is
        // defensive: a refused use is silent, like the ownership guard above.
        var store = services.GetRequiredService<GameDataStore>();
        var itemData = store.GetItem(used.ItemId);
        var refusal = ItemUseRules.CanUse(itemData, used.StorageType, player.Level, player.Job, player.Gender,
            player.IsDead, TradeSessionService.Instance.Get(player.SessionId) is not null);
        if (refusal != ItemUseRefusal.None)
        {
            Logger.LogInformation("{Name} used item {Item} (uid {Uid}); refused: {Why}", player.Name, used.ItemId, itemUid, refusal);
            return;
        }
        if (!ItemUseCooldown.TryAcquire(player.CharacterId, used.ItemId, Environment.TickCount64))
        {
            Logger.LogInformation("{Name} used item {Item} again inside {Ms} ms; refused", player.Name, used.ItemId, ItemUseCooldown.MinGapMs);
            return;
        }

        // Map the item's flat inventory Slot to the client's (bag, slot) — the SAME mapping as
        // WriteItemInstance (main bag = slots 50-69, extra bags 2-4 = 0-34; worn gear 0-49). The 2329
        // handler on the client refreshes exactly that (bag, slot) cell, so it must be the real one.
        // CanUse admitted StorageType 0 only, so the flat slot is always a bag cell.
        byte bagIx, slotIx;
        {
            var flat = used.Slot < 0 ? 0 : (int)used.Slot;
            if (flat < BagCapacity.MainBagCells) { bagIx = MainBagContainer; slotIx = (byte)(MainBagSlotBase + flat); }
            else
            {
                var i = flat - BagCapacity.MainBagCells;
                bagIx = (byte)Math.Clamp(FirstExtraClientBag + i / BagCapacity.ExtraBagStride, FirstExtraClientBag, LastExtraClientBag);
                slotIx = (byte)(i % BagCapacity.ExtraBagStride);
            }
        }

        // Resolved before the 2329: what the item's type or an active quest makes the server send goes out
        // AHEAD of it. A use is answered with the 2329 last (kit: 2336 -> 2498 -> 2329;
        // remote-bank scroll: 2864 -> 2329; quest item: 2428 -> 2427 -> [payout] -> 2329).
        if (itemData is { Item_Type: RepairKitItemType })
            await RepairWornGearAsync(session, player, inventory, store, services.GetRequiredService<EffectService>());

        // 2864 NfyOpenRemoteBank opens the bank window from the data the client already holds (it asks 1340
        // ReqPCBankData on every map load and ReqPCBankDataHandler answers 2338), so the body is only the
        // scroll's template id: NONE family, fixed 4 bytes, body+0 = ItemInfo.ID of the scroll that was used
        // (always an Item_Type 43 row). It is sent right after the 1323, and
        // the next S2C frame is the 2329.
        if (itemData is { Item_Type: RemoteBankScrollItemType })
        {
            // INV-H3: this path opens the bank window without a 1341, so it sets the mark 1346 requires. Not a packet:
            // the 2864 -> 2329 order is untouched.
            BankOpenHandler.MarkOpen(player);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyOpenRemoteBank, writer => writer.WriteUInt32(used.ItemId));
        }

        // Skill-reset scroll: 2685 -> 2488 -> 2544, all ahead of the 2329 (ResetSkillsAsync).
        if (itemData is { Item_Type: SkillResetScrollItemType })
            await ResetSkillsAsync(session, player, player.Job, store, services, Logger);

        // Storage Slot Expansion (Item_Type 39, Bag_Size 20): the bank grows by the item's Bag_Size, answered by 2345
        // ahead of the 2329 (1323 -> 2345 -> 2329: 20 -> 40,
        // 40 -> 60, and the next 2310 of the same character carries the new count at record+121). Stored
        // before anything is sent, so a relog keeps it. A bank already at its top (the client refuses to send the use
        // there) keeps the item: nothing is consumed and nothing answered.
        if (itemData is { Item_Type: BankExpansion.ExpansionItemType })
        {
            if (player.Character is not { } bankOwner)
            {
                Logger.LogWarning("{Name} used bank expansion {Item} with no character snapshot: not consumed", player.Name, used.ItemId);
                return;
            }
            byte before = BankExpansion.SlotsOf(bankOwner);
            byte after = BankExpansion.Expanded(before, itemData.Bag_Size);
            if (after <= before)
            {
                Logger.LogInformation("{Name} used bank expansion {Item} with the bank at {Slots} slots: not consumed",
                    player.Name, used.ItemId, before);
                return;
            }
            try
            {
                await BankExpansion.PersistAsync(services.GetRequiredService<ICharacterRepository>(), player.CharacterId, after);
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning(ex, "{Name}: could not store the bank expansion; item not consumed", player.Name);
                return;
            }
            bankOwner.BankSlots = after;
            await session.SendRmiAsync(ZoneS2COpcodes.OnBankBagSizeChange,
                writer => writer.WriteBytes(BankExpansion.BuildBankBagSizeChange2345(actorHandle, after)));
            Logger.LogInformation("{Name} expanded the bank {Before} -> {After} slots with item {Item}",
                player.Name, before, after, used.ItemId);
        }

        // Reins (Item_Type 11): the item's vehicle joins the character's list (VehicleRules.Grant has the
        // rule), answered with the 2523 quick slot and the full updated 2522 ahead of the 2329
        // (1323 -> 2523 -> 2522 -> 2329). Stored before anything is sent. A reins item whose vehicle or
        // duration the tables do not give, or whose grant cannot be stored, is not consumed and gets no answer.
        if (itemData is { Item_Type: VehicleRules.ReinsItemType })
        {
            if (!VehicleRules.TryResolveReins(store, used.ItemId, out var ridingId, out var days))
            {
                Logger.LogWarning("{Name} used reins item {Item}, which no RidingDurationGroup/RidingInfo/ItemCashInfo row resolves: not consumed",
                    player.Name, used.ItemId);
                return;
            }
            var nowOle = VehiclePackets.NowOle();
            IReadOnlyList<OwnedVehicle> vehicles;
            try
            {
                vehicles = await services.GetRequiredService<IVehicleRepository>().GrantAsync(player.CharacterId, ridingId, days, nowOle, ct);
            }
            catch (System.Exception ex) when (ex is not System.OperationCanceledException)
            {
                Logger.LogWarning(ex, "{Name}: could not store vehicle {Vehicle} from reins {Item}; item not consumed",
                    player.Name, ridingId, used.ItemId);
                return;
            }
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnVehicleQuickSlot, VehiclePackets.BuildVehicleQuickSlot2523Empty());
            VehicleClientList.Record(session.SessionId, vehicles); // before the send: see VehicleClientList (2838 guard)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAllVehicleList, VehiclePackets.BuildAllVehicleList2522(vehicles, nowOle));
            Logger.LogInformation("{Name} used reins {Item}: vehicle {Vehicle} {Duration}",
                player.Name, used.ItemId, ridingId, days is { } d ? $"+{d} days" : "permanent");
        }

        // Crafting blueprint (a CraftScroll row names the item; Item_Type 19 alone is not enough): its recipe is
        // learned, answered 2538 -> 2435 (balance unchanged) ahead of the 2329
        // (CraftScrollLearning has the details). A blueprint that
        // teaches nothing new (another profession, already known) or cannot be stored is not consumed and gets no
        // answer.
        if (CraftScrollLearning.TryResolve(store, used.ItemId) is { } blueprintRecipe
            && !await CraftScrollLearning.TryLearnAsync(session, player, store,
                services.GetRequiredService<RO2.Economy.Domain.ICraftRecipeRepository>(), blueprintRecipe, used.ItemId, Logger, ct))
            return;

        var questTicked = await AdvanceUseItemQuestsAsync(session, player, used.ItemId, store, services);

        // Consume one from the stack and persist. remainingCount is the stack size AFTER the use; the
        // client writes it into the slot and clears the slot when it reaches 0. (BuildUseItem2329
        // already emits a valid compact marker so the client does not NULL-deref.) Done after the quest
        // payout above (the used item's decrement comes last): a reward placed first
        // cannot be auto-assigned the cell this use empties, which the 2329 below would then clear.
        var remaining = used.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(itemUid); remaining = 0; }
        else { used.Count = remaining; await inventory.UpdateItemAsync(used); }

        // A held-item Khara row naming the used item takes the new count, its 2577 ahead of the 2329. Reward items a
        // Reward_Auto quest pays above get theirs in QuestPrizeHandler.GrantRewardsAsync.
        await KharaService.OnHeldItemChangedAsync(session, player, used.ItemId, services, Logger, ct);
        // A "Used @1" Khara (Complete_Type 6) counts the item used up here too, ahead of the 2329. This follows
        // the 1561 item skill's rule (SkillStartItemHandler); the rows' items are Item_Type 101 skill items, plus one 17 and two 38.
        await KharaService.OnItemUsedAsync(session, player, used.ItemId, services, Logger, ct);

        // Stat-reset scroll (ItemInfo Item_Type 25, "능력치 초기화 주문서"): refund all allocated bonus
        // stat points. These items carry no Skill/Effect id — the reset is special-cased by type. Zero the
        // allocation, reset primaries to class base, persist, and re-push the 2498 sheet: the Bonus column
        // empties and the unspent pool restores to the full level curve automatically (pool is derived).
        // The scroll is answered 1323 -> 2595 -> 2498 -> 2329, with nothing in between. The 2595 carries
        // 1,1,1,1,1 at blob+14..+22 and the 2498 a zeroed bonus block (blob+26..+36), and the 2595's blob+72/+76
        // equal that 2498's current HP/SP. WorldEntryService.BuildPcInitTrait2595 of the reset character and the
        // sheet sent after it is exactly that: 1 + a bonus of 0 per primary, and the 2498's vitals. So the pair
        // goes out here, ahead of the 2329, as it does for an accepted 1493.
        if (itemData is { Item_Type: StatResetScrollItemType } && player.Character is { } c
            && StatService.ResetBonusStats(store, c))
        {
            player.Str = c.Str; player.Agi = c.Agi; player.Int = c.Int;
            player.Wis = c.Wis; player.Vit = c.Vit; player.Luk = c.Luk;
            try { await services.GetRequiredService<ICharacterRepository>().SaveCharacterAsync(c); }
            catch (System.Exception ex) { Logger.LogWarning(ex, "{Name}: could not persist stat reset", player.Name); }
            var equipped = await inventory.GetEquipmentAsync(c.Id);
            // Refresh the combat cache too, or the sheet drops but combat keeps rolling the pre-reset
            // crit/parry and dealing the pre-reset damage until relog — a stat-reset exploit. The reset
            // lowers VIT/WIS, so the runtime max drops with it and the current values are clamped BEFORE
            // the 2498, which carries them: otherwise RegenService and every 2401 keep the old max.
            // EffectService.RefreshPcTraitSheet does all three from one sheet, active effects included.
            var resetTrait = services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, c, equipped);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait,
                WorldEntryService.BuildPcInitTrait2595(c, resetTrait));
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, resetTrait);
            Logger.LogInformation("{Name} used a stat-reset scroll: bonus points refunded", player.Name);
        }

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            writer => writer.WriteBytes(CombatPackets.BuildUseItem2329(
                actorHandle: actorHandle,
                slotIndex: slotIx,
                bagId: bagIx,
                itemUid: itemUid,
                remainingCount: (byte)Math.Clamp(remaining, 0, byte.MaxValue),
                useSource: CombatPackets.UseSourceItemUse)));

        // 2330 OnUseItemNotify, so the players around play the use animation. It carries the ItemInfo
        // TEMPLATE id (used.ItemId), not the uid (LootNotifyPackets.BuildUseItemNotify2330).
        // NOT to the user itself: a 1323 is not answered with a 2330 carrying the user's own handle (that
        // only follows a 1561 item-consuming skill cast: 1561 -> 2329 -> 2330 -> 2715). The source session is excluded.
        // Not at all for a repair kit or a remote-bank scroll (1323 -> 2336 and 2864 -> 2329 carry no 2330).
        // Nor for the use that ticked a quest objective: the sequence stops at 2428 -> 2427 -> 2329.
        if (!questTicked && itemData is not { Item_Type: RepairKitItemType or RemoteBankScrollItemType })
        {
            var notifyBody = LootNotifyPackets.BuildUseItemNotify2330(player.EntityId, used.ItemId);
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnUseItemNotify,
                writer => writer.WriteBytes(notifyBody), excludeSessionId: player.SessionId);
        }

        // If the consumed item declares a timed effect (ItemInfo.Effect_ID_1 != 0), apply it:
        // EffectService broadcasts 2492 OnAddEffect now and expires it later with 2493/2494. The
        // effect id, granting skill and duration all come from the item's own master-data columns.
        if (itemData is { Effect_ID_1: not 0 })
        {
            var effects = services.GetRequiredService<EffectService>();
            await effects.ApplyItemEffectAsync(player, itemData);
        }
    }

    /// <summary>
    /// A repair kit (ItemInfo Item_Type 27) used from the bag: every worn item that has durability comes back
    /// full, answered by 2336 OnRepairItem and the 2498 sheet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every record in the 2336 is a worn one (bag byte record+15 == 0) and comes back at
    /// its ItemInfo.Durability (10000), n = 1..12. Which worn items: the gear slots 1-12
    /// (ItemInfo.Durability 10000) are repaired; worn costumes (slots 14-19 and 25-26, ItemInfo.Durability
    /// 0) and cards (slots 20-24, Item_Type 13, Durability 10000) are left out. So: worn, ItemInfo.Durability &gt; 0, not a
    /// card, whatever the current durability, in slot order.
    /// </para>
    /// <para>
    /// A kit is free, so the head's wallet (head+112) is the unchanged balance. When nothing worn qualifies no
    /// 2336 goes out, since an empty repair has no defined shape.
    /// </para>
    /// </remarks>
    private async Task RepairWornGearAsync(ProudSession session, PlayerState player, IInventoryRepository inventory,
        GameDataStore store, EffectService effects)
    {
        var worn = await inventory.GetEquipmentAsync(player.CharacterId);
        var records = new List<byte[]>();
        foreach (var item in worn.OrderBy(i => i.Slot))
        {
            var info = store.GetItem(item.ItemId);
            if (info is null || info.Durability == 0 || info.Item_Type == CardItemType) continue;
            var maxDur = (short)Math.Min(info.Durability, (uint)short.MaxValue);
            if (item.Durability != maxDur)
            {
                item.Durability = maxDur;
                await inventory.UpdateItemAsync(item);
            }
            // With the runehole block, as RepairItemHandler: the client re-reads each item from this record.
            records.Add(ItemWorkbench.RecordWithRuneholes(item, store));
        }
        if (records.Count == 0)
        {
            Logger.LogDebug("{Name} used a repair kit with nothing worn to repair", player.Name);
            return;
        }

        var repairBody = RepairItemPackets.BuildOnRepairItem2336(player, records, store);
        await session.SendRmiAsync(RepairItemPackets.OnRepairItem2336, writer => writer.WriteBytes(repairBody));

        // Every 2336 is followed by a 2498, kit or NPC. It keeps the active
        // effects, and the runtime max and combat cache follow it (EffectService.RefreshPcTraitSheet), as after an
        // NPC repair (RepairItemHandler).
        if (player.Character is { } c)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, effects.RefreshPcTraitSheet(player, c, worn));

        Logger.LogInformation("{Name} used a repair kit: {Count} worn items repaired", player.Name, records.Count);
    }

    /// <summary>
    /// A skill reset to <paramref name="job"/>: every talent-tree skill goes back to the class's starter skills
    /// at level 1 (<see cref="TalentRules.StarterSkillGroups"/>), the skills come off the quick-slot bar (2685),
    /// and the new list (2488) and talent pool (2544) are sent, in that order. Sent for a skill-reset scroll
    /// (ItemInfo Item_Type 26) and for a quest job change whose JobChange_Table row has Talent_Reset 1
    /// (QuestPrizeHandler.ApplyQuestJobChangeAsync, with the NEW job).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scroll use is answered <c>1323 -> 2685 -> 2488 -> 2544 -> 2329</c> (optionally with a 2498 before the 2329);
    /// a quest job change sends the same three, <c>2685 -> 2488 -> 2544</c>.
    /// After the 2544 the active Init_Handling 0 effects end (<see cref="TalentResetEffects"/>), ahead of the 2329.
    /// </para>
    /// <para>
    /// The 2488 after it is the starter set at level 1 plus what is not a talent skill. Example: Thief (job 4)
    /// [410101, 410301, 410801], Soul Maker (18) [630101, 630901, 631701], and Warrior (6)
    /// [110201, 111001, 114401, 5030003]: the Swordsman rows back at level 1 and 50300, a Blacksmith profession
    /// skill that has no Talent_Table row, kept at level 3; the job-master skill 1200 goes. Job changes work the
    /// same way: Ranger (10) [310401, 310701, 312401, 5030001] and Crescentia (17) [610101, 610401,
    /// 611101]. All are TalentRules.StarterSkillGroups of the character's job (the current job for a scroll,
    /// the new Job_ID for a job change). Talent_Table rows outside that set are deleted from
    /// character_skills; skills with no Talent_Table row are kept at their level.
    /// </para>
    /// <para>
    /// The 2544's blob+10 is the pool after the reset, <see cref="TalentRules.TalentPointsRemaining"/>: with only
    /// starters left in the tree it is level - 1. Returned so the caller can log it.
    /// </para>
    /// </remarks>
    internal static async Task<byte> ResetSkillsAsync(ProudSession session, PlayerState player, byte job,
        GameDataStore store, IServiceProvider services, ILogger logger)
    {
        var starters = TalentRules.StarterSkillGroups(store, job);
        var talentGroups = TalentRules.TalentSkillGroups(store);
        var dropped = player.LearnedSkills.Keys
            .Where(g => talentGroups.Contains(g) && Array.IndexOf(starters, g) < 0)
            .ToArray();

        // Stored first: if a write throws, nothing is sent (a scroll is then not consumed: no 2329 goes out),
        // and the next login re-seeds any starter that is missing (LoginPCHandler).
        if (dropped.Length > 0)
        {
            await services.GetRequiredService<ISkillRepository>().DeleteSkillsAsync(player.CharacterId, dropped);
        }
        var skillRepo = services.GetRequiredService<ISkillRepository>();
        foreach (var group in starters)
        {
            if (!player.LearnedSkills.TryGetValue(group, out var level))
                await skillRepo.LearnSkillAsync(player.CharacterId, group, 1);
            else if (level != 1)
                await skillRepo.UpdateSkillLevelAsync(player.CharacterId, group, 1);
        }
        player.ResetLearnedSkills(dropped, starters);

        // Every skill cell of the bar, starters included (see JobChangePackets.BuildDelQuickSlots2685), also
        // cleared from the stored bar so the next world-entry 2472 leaves them out. A 2685 with no cells is
        // never sent, so none goes out when the bar holds no skill.
        var bar = services.GetRequiredService<IQuickSlotRepository>();
        var cells = (await bar.GetByCharacterAsync(player.CharacterId))
            .Where(q => q.EntryType == SkillQuickSlotEntryType)
            .OrderBy(q => q.Page).ThenBy(q => q.Slot)
            .Select(q => (q.Page, q.Slot))
            .ToList();
        foreach (var (page, slot) in cells)
            await bar.ClearAsync(player.CharacterId, page, slot);
        if (cells.Count > 0)
            await session.SendRmiAsync(ZoneS2COpcodes.NfyChangeProJobDelQuickSlot,
                writer => writer.WriteBytes(JobChangePackets.BuildDelQuickSlots2685(cells)));

        // Ascending by skill id (the builder sorts), and with the
        // guild's line skills, as every 2488 carries them (GuildSkillBookLearn.SkillListFor). The talent pool
        // below counts the character's own skills only.
        var skills = player.LearnedSkills.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray();
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnUseSkillList,
            WorldEntryService.BuildUseSkillList2488(GuildSkillBookLearn.SkillListFor(player, store)));

        var pool = TalentRules.TalentPointsRemaining(store, job, player.Level, skills);
        await session.SendRmiAsync(ZoneS2COpcodes.OnInitTalentPoint,
            writer => writer.WriteBytes(QuestProgressPackets.BuildInitTalentPoint2544(player.EntityId, pool)));

        // Right after the 2544, and so before the scroll's 2329, the reset ends every active effect whose
        // Effect_Table.Init_Handling is 0, each 2498 -> 2493 -> 2494, ascending, the ride with its dismount
        // (no Init_Handling 1 effect is removed; see TalentResetEffects). The job-change path gets it too.
        var ended = await TalentResetEffects.DropInitEffectsAsync(session, player, store, services);

        logger.LogInformation("{Name} skill reset for job {Job}: {Dropped} skills dropped, {Cells} bar cells cleared, {Pool} talent points, {Ended} effects ended",
            player.Name, job, dropped.Length, cells.Count, pool, ended);
        return pool;
    }

    /// <summary>
    /// The "use item" quest objective (Complete_Type 6): using the item a held quest names ticks it with 2428
    /// OnQuestInCompleteNotify and, once every such row of the quest is met, closes it with 2427; a
    /// <c>Reward_Auto</c> quest is then paid on the spot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="QuestItemObjectivePackets"/>: the 2427 is the next frame after the 2428 and the count is 1.
    /// Quests with <c>Reward_Auto</c> 1 go on <c>2333 x reward items -> 2429 -> ... -> 2435</c>
    /// with no 1378 (e.g. quest 111150004: 2427 -> 4 x 2333 -> 2429 -> 2725 -> 2435); the others are
    /// followed by the 2329 and paid at a later 1378, which then sends no second 2427 (QuestPrizeHandler's
    /// ObjectiveClosedThisSession knows type 6).
    /// </para>
    /// <para>
    /// How many uses a row needs: <c>Complete_Type_Value1</c> and <c>Complete_Type_Value3</c> are both 1 on most
    /// type-6 rows, so those quests close on one use whichever column counts. They differ
    /// only on 411000087 (Value3 10, and its accept hands out Action_Value2 = 10 of the item) and on row 1 of
    /// 231010070 (Value3 2). Value3 is used: it is the only one
    /// of the two that varies, 411000087 gives exactly Value3 items to use, and it can only make a quest close
    /// later, never earlier. Uses are counted per row (<see cref="QuestUseItemProgress"/>), because
    /// 231010070 has five type-6 rows with five items.
    /// </para>
    /// </remarks>
    private async Task<bool> AdvanceUseItemQuestsAsync(ProudSession session, PlayerState player, uint itemId,
        GameDataStore store, IServiceProvider services)
    {
        var anyTicked = false;
        // Snapshot: the sends are async, and the payout below edits the set.
        foreach (var questId in player.ActiveQuests.ToArray())
        {
            if (!store.QuestRows.TryGetValue(questId, out var rows)) continue;

            var ticked = false;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Complete_Type != UseItemObjective || row.Complete_Type_Value2 != itemId) continue;
                if (QuestUseItemProgress.AddUse(player, questId, rows.Length, i, UsesNeeded(row)) is not { } count)
                    continue;   // this row was already met
                ticked = true;
                var tickBody = QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(
                    questId, itemId, (ushort)Math.Min(count, ushort.MaxValue));
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInCompleteNotify, writer => writer.WriteBytes(tickBody));
            }
            if (!ticked) continue;
            anyTicked = true;

            // Closed when every objective row is a met use-item row (no type-6 quest has another objective
            // type on its other rows; they are all Complete_Type 0).
            var closed = true;
            for (int i = 0; i < rows.Length && closed; i++)
            {
                if (rows[i].Complete_Type == NoObjective) continue;
                closed = rows[i].Complete_Type == UseItemObjective
                    && QuestUseItemProgress.Count(player, questId, i) >= UsesNeeded(rows[i]);
            }
            if (!closed)
            {
                // The uses so far are stored with the quest (QuestProgressStore), so a relog keeps them.
                await QuestProgressStore.SaveObjectiveStateAsync(services.GetRequiredService<IQuestRepository>(),
                    player, questId, store, Logger);
                continue;
            }

            player.QuestKillProgress[questId] = ObjectiveClosedMark;
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));
            Logger.LogInformation("{Name} quest {QuestId}: use-item objective met with item {Item}",
                player.Name, questId, itemId);

            if (!QuestInfoExtraColumns.IsRewardAuto(questId) || !store.Quests.TryGetValue(questId, out var quest))
            {
                // Paid at a later 1378: the close is stored with the quest, so a relog loads it at state 1 and that
                // turn-in sends no 2427.
                await QuestProgressStore.SaveObjectiveStateAsync(services.GetRequiredService<IQuestRepository>(),
                    player, questId, store, Logger);
                continue;
            }
            // Removed before the awaits, like QuestPrizeHandler, so a 1378 racing this cannot pay it twice.
            if (!player.ActiveQuests.Remove(questId)) continue;
            player.QuestKillProgress.Remove(questId);
            QuestUseItemProgress.Reset(player, questId);
            await services.GetRequiredService<IQuestRepository>().CompleteQuestAsync(player.CharacterId, questId);
            await QuestPrizeHandler.GrantRewardsAsync(
                session, player, quest, QuestPrizeHandler.NoRewardChoice, services, Logger);
            Logger.LogInformation("{Name} quest {QuestId}: paid on the spot (Reward_Auto)", player.Name, questId);
        }
        return anyTicked;
    }

    /// <summary>Uses a type-6 row needs: <c>Complete_Type_Value3</c>, 0 read as 1 (see AdvanceUseItemQuestsAsync).</summary>
    private static int UsesNeeded(QuestData row) => (int)Math.Clamp(row.Complete_Type_Value3, 1u, MaxUsesPerRow);
}

/// <summary>Why <see cref="ItemUseRules.CanUse"/> refused a 1323; <see cref="None"/> = allowed.</summary>
internal enum ItemUseRefusal
{
    None,
    UnknownTemplate,
    /// <summary>The row is not in the bag (StorageType 0): worn, bank and bag-window rows cannot be used.</summary>
    NotInBag,
    Dead,
    Trading,
    /// <summary>A worn-gear template (weapon, armour, accessory, quiver, costume) or an equippable bag: not a usable item.</summary>
    NotUsableType,
    LevelTooLow,
    JobNotAllowed,
    SexNotAllowed,
}

/// <summary>
/// Pure INV-H7 pre-gate of <see cref="ItemUseHandler"/> (the handler is not test-instantiable).
/// Every rule is defensive. No ItemInfo column says which Item_Types a 1323 may act on, so the type rule is a
/// NEGATIVE list (the gear classes <c>EquipRules</c> already knows) rather than a positive whitelist: a wrong
/// whitelist would silently break a real consumable, a wrong negative list only lets an inert item through.
/// </summary>
internal static class ItemUseRules
{
    // ItemInfo Item_Type of the worn-gear classes (same classes as EquipRules.ItemTypeFits): 1 weapon, 6 tool,
    // 2 armour, 3 / 4 / 52 accessories, 104 quiver, 7 equippable bag.
    private const byte WeaponItemType = 1;
    private const byte ArmourItemType = 2;
    private const byte Accessory3ItemType = 3;
    private const byte Accessory4ItemType = 4;
    private const byte ToolItemType = 6;
    private const byte EquippableBagItemType = 7;
    private const byte Accessory52ItemType = 52;
    private const byte QuiverItemType = 104;

    /// <summary>InventoryItem.StorageType of a bag row; worn, bank and bag-window rows use other values.</summary>
    private const byte BagStorageType = 0;

    /// <summary>Require_Sex values that restrict an item; any other value (0, 3) means no restriction.</summary>
    private const byte SexMale = 1;
    private const byte SexFemale = 2;

    /// <summary>Highest job id: Require_Job is a 64-bit mask with bit <c>job - 1</c> per job.</summary>
    private const int MaxJobId = 63;

    /// <summary>Item_Types that are worn gear when they carry an Equip_Slot (EquipRules.ItemTypeFits): 1/6 weapon and tool,
    /// 2 armour, 3/4/52 accessory, 104 quiver. 7 is the equippable bag.</summary>
    private static bool IsGear(ItemData t) =>
        t.Item_Type == EquippableBagItemType
        || (t.Equip_Slot != 0 && t.Item_Type is WeaponItemType or ArmourItemType or Accessory3ItemType or Accessory4ItemType
            or ToolItemType or Accessory52ItemType or QuiverItemType);

    /// <summary>
    /// Checks, in this order: known template, bag row, alive, not in a live trade, usable type, then ItemInfo's
    /// Require_Level / Require_Job (bit job-1) / Require_Sex (1 male, 2 female; 3 and 0 mean no restriction) -- the same
    /// reading EquipRules.CheckRequirements applies to equipping.
    /// </summary>
    internal static ItemUseRefusal CanUse(ItemData? t, byte storageType, int level, int job, int sex, bool dead, bool trading)
    {
        if (t is null) return ItemUseRefusal.UnknownTemplate;
        if (storageType != BagStorageType) return ItemUseRefusal.NotInBag;
        if (dead) return ItemUseRefusal.Dead;
        if (trading) return ItemUseRefusal.Trading;
        if (IsGear(t)) return ItemUseRefusal.NotUsableType;
        if (level < t.Require_Level) return ItemUseRefusal.LevelTooLow;
        if (t.Require_Job != 0 && (job < 1 || job > MaxJobId || (t.Require_Job & (1L << (job - 1))) == 0))
            return ItemUseRefusal.JobNotAllowed;
        if (t.Require_Sex is SexMale or SexFemale && sex != t.Require_Sex) return ItemUseRefusal.SexNotAllowed;
        return ItemUseRefusal.None;
    }
}

/// <summary>
/// Per-(character, template) minimum gap between two accepted 1323s. ItemInfo has no
/// cooldown column, so the gap is a flat anti-burst value. Session-only, never persisted.
/// </summary>
internal static class ItemUseCooldown
{
    internal const long MinGapMs = 200;

    private const int PruneAbove = 2048;
    private const long StaleMs = 60_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Character, uint Template), long> LastUse = new();

    /// <summary>True (and records <paramref name="nowMs"/>) when the last accepted use of this template by this character
    /// was at least <see cref="MinGapMs"/> ago, or never; false while it is inside the gap (the use is not recorded).</summary>
    internal static bool TryAcquire(uint characterId, uint templateId, long nowMs)
    {
        lock (Gate)
        {
            var key = (characterId, templateId);
            if (LastUse.TryGetValue(key, out var last) && nowMs - last < MinGapMs) return false;
            if (LastUse.Count >= PruneAbove)
                foreach (var stale in LastUse.Where(kv => nowMs - kv.Value > StaleMs).Select(kv => kv.Key).ToList())
                    LastUse.Remove(stale);
            LastUse[key] = nowMs;
            return true;
        }
    }
}
