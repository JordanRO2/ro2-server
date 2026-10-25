using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1561 ReqSkillStart_Item — cast the skill an item carries (potions, warp scrolls, Dungeon Wing).
/// </summary>
/// <remarks>
/// <para>
/// Request, a fixed 55 bytes: <c>+0 u32 skillId | +4 f32 caster XYZ | +16 u32 caster handle | +20 u8 1 |
/// +21 u64 | +29 4 zero bytes | +33 f32 XYZ | +45 u16 | +47 u64 itemUid</c>.
/// The item the cast consumes is the u64 at +47; the u32 at +21 is the low half of the +21 u64, not an item id.
/// </para>
/// <para>
/// The answer to an accepted request is always the same four messages in the same order:
/// <c>2329 OnUseItem → 2330 OnUseItemNotify → 2715 AnsUseSkillStart → 2716 NfyUseSkillStart</c>,
/// with the fields below:
/// </para>
/// <list type="bullet">
/// <item>2329 carries the request's +47 uid, result 0, the caster handle, and <c>useSource 0</c>
/// (never the 1 the 1323 path uses); its count decrements by exactly 1 per use of the same uid.</item>
/// <item>2330's item template is the ItemInfo row whose <c>Skill_ID_1</c> is the request's skill
/// (for example 21330037→9110001, 21117020→9000211, 21330015→9100201, 60000988→9100701), so the uid names
/// the item that owns the skill.</item>
/// <item>2715's handle at +6 is request +16, so +10 is 1; the variant at +27 is request +45; the
/// vec3 echoes request +33; and the cooldown equals <c>Skill_Table_SkillLevel.Cooldown_Time</c>
/// verbatim.</item>
/// <item>2716 names the caster at +12 and request +16 as the target (+49, with +48 = 1), request
/// +45 as the variant (+53), zero extra targets, 65-byte blob.</item>
/// </list>
/// <para>
/// When the used item declares an effect (ItemInfo.Effect_ID_1), that effect's application run comes
/// first, ahead of the 2329: <c>2499 × N → 2498 → 2492 → 2329 → 2330 → 2715 → 2716</c>. See the effect block
/// in
/// <see cref="HandleAsync"/>.
/// </para>
/// <para>
/// +16 and +45 are ECHOED, not derived. Most items are used on oneself (+16 is the caster and +45 is
/// usually 0), but +16 is not always the caster: for example 9200101 Yggdrasil Leaf targets a dead PC (the casts
/// behind the 3016 offers in RevivalSkillPackets). The target matters beyond the animation: the client takes
/// the 1509's primary target (1509 +5) from 2715 +6. A leaf answered with the caster at +6 makes the client's
/// 1509 name the caster, and SkillDamageHandler then has no dead PC to offer the revival to.
/// </para>
/// <para>
/// A refused request is not silent: it gets <b>2711 NfySkillCastingCancel</b>
/// naming the request's own caster (+16) and skill (+0), body
/// <c>01 10 1000 49e4 00000000 [caster] [skill]</c>, and nothing from the item path: no
/// 2329 (the item is not consumed), no 2330, and no 2715/2716 for that skill. So the
/// refusal below (item missing, not the caster's, or not carrying the requested skill) sends that
/// 2711 to the caster, which is how the client's pending item cast is closed. Bystanders were
/// never told a cast started (no 2716 is sent on refusal), so it is unicast here.
/// </para>
/// <para>
/// THE REPEAT REFUSAL. The client may fire an item skill again within a very short time, the same
/// one or another. A 1561 that arrives less than 1,000 ms after the caster's last ANSWERED 1561, of ANY item skill,
/// is refused here with the lone 2711 before anything is read or consumed (<see cref="ItemSkillRepeatGuard"/>).
/// A per-skill window would answer cross-skill repeats, each consuming one more item and sending
/// 2329 / 2330 / 2715 / 2716 (and the effect and the ride drop) where a lone 2711 belongs. Beyond the repeat
/// window this server refuses only on the ownership/skill checks it can make.
/// </para>
/// <para>
/// THE WARP ITEMS MOVE THE PLAYER, two ways (see <see cref="ItemWarpSkills"/>). The
/// Butterfly Wing (9100101, to the bind point) and the Dungeon Wing (9110001, to the current map's Map_List return
/// point) move at this answer: the departure follows the 2716 at once, and
/// their 1509 then gets no 2718 (SkillDamageHandler). The town warps 9100201
/// Prontera, 9100301 Alberta, 9100701 Morroc, 9100801 Training Ground and 9101001 Jawaii move when their 1509 lands,
/// after its 2718: this answer only grants that 1509
/// the move (<see cref="ItemWarpSkills.GrantLanding"/>). The Dungeon Wing is refused with the lone 2711 where it has
/// nowhere to go, before anything is consumed: on a Map_Type 0 map, or one whose return point is missing. The form
/// of the move follows the source map: from an instance (Map_Type 1) the 2556 hand-off and the client's
/// reconnect. Not covered: 9100401 / 9100501 / 9100601 / 9100901 (no destination: no move).
/// </para>
/// </remarks>
public class SkillStartItemHandler : ZoneRmiHandlerBase<SkillStartItemHandler>
{
    /// <summary>Fixed request size.</summary>
    private const int RequestLen = 55;

    // ---- Request field offsets (see the class remarks) ----

    /// <summary>Request +0: u32 skill id the item carries.</summary>
    private const int ReqSkillIdOffset = 0;

    /// <summary>Request +16: u32 handle of the cast's target (the caster itself for a self-use item).</summary>
    private const int ReqTargetHandleOffset = 16;

    /// <summary>Request +33: f32 X, then Y at +37 and Z at +41 (the vec3 echoed into 2715).</summary>
    private const int ReqPositionOffset = 33;

    /// <summary>Request +45: u16 cast variant, echoed into 2715 and 2716.</summary>
    private const int ReqVariantOffset = 45;

    /// <summary>Request +47: u64 uid of the item the cast consumes.</summary>
    private const int ReqItemUidOffset = 47;

    // ---- InventoryItem.StorageType values ----

    /// <summary>The bag: the only storage a consumable can be used from.</summary>
    private const byte StorageTypeBag = 0;

    /// <summary>Worn equipment.</summary>
    private const byte StorageTypeEquipped = 1;

    /// <summary>The bank.</summary>
    private const byte StorageTypeBank = 2;

    /// <summary>The extra-bag items themselves (character-bag cells 0..2).</summary>
    private const byte StorageTypeExtraBagItem = 3;

    // ---- Client (bag, slot) cell layout, as the item record and 2329 carry it ----

    /// <summary>Container 0: worn equipment, wire slots 0..49.</summary>
    private const byte EquipmentContainer = 0;

    /// <summary>Container 1: the main bag (wire slots 50..69) and the extra-bag items (wire slots 70..72).</summary>
    private const byte MainBagContainer = 1;

    /// <summary>First extra-bag container; extra bag n is container 2 + n.</summary>
    private const byte FirstExtraBagContainer = 2;

    /// <summary>Last extra-bag container (one per extra-bag cell, <see cref="BagCapacity.ExtraBagCount"/> in all).</summary>
    private const byte LastExtraBagContainer = FirstExtraBagContainer + BagCapacity.ExtraBagCount - 1;

    /// <summary>Container 5: the bank.</summary>
    private const byte BankContainer = 5;

    /// <summary>Wire slot of the first main-bag cell in container 1; slots below it are equipment slots.</summary>
    private const byte MainBagFirstWireSlot = 50;

    /// <summary>Wire slot of the first extra-bag item in container 1.</summary>
    private const byte ExtraBagItemFirstWireSlot = 70;

    /// <summary>
    /// 2329 +21 on the item-skill path: 0. (1323 ReqPCItemUse's
    /// answer carries 1.)
    /// </summary>
    private const byte ItemSkillUseSource = 0;

    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillStart_Item;
    public override string Name => "SkillStartItem";

    public SkillStartItemHandler(IServiceScopeFactory s, ILogger<SkillStartItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLen) return;
        var req = payload.Span;

        var skillId = BinaryPrimitives.ReadUInt32LittleEndian(req[ReqSkillIdOffset..]);
        // +33 triple: echoed into 2715 exactly as the 1508 path does.
        var posX = BinaryPrimitives.ReadSingleLittleEndian(req[ReqPositionOffset..]);
        var posY = BinaryPrimitives.ReadSingleLittleEndian(req[(ReqPositionOffset + 4)..]);
        var posZ = BinaryPrimitives.ReadSingleLittleEndian(req[(ReqPositionOffset + 8)..]);
        var itemUid = BinaryPrimitives.ReadUInt64LittleEndian(req[ReqItemUidOffset..]);

        // The CAST's target, echoed into 2715 +6 and 2716 blob+49: the
        // caster for a self-use item, the dead PC for Yggdrasil Leaf. The client's 1509 takes its primary
        // target from 2715 +6, so this echo is what lets the leaf's landing reach that PC. Trusting it
        // is what SkillStartHandler does for 1508: it only names who the animation and the 1509 aim at,
        // and SkillDamageHandler checks the 1509's target itself (a revival is offered only to a dead PC
        // on the caster's map that is not the caster).
        var targetHandle = BinaryPrimitives.ReadUInt32LittleEndian(req[ReqTargetHandleOffset..]);
        // 2715 +27 / 2716 blob+53 echo request +45 (class remarks).
        var variant = BinaryPrimitives.ReadUInt16LittleEndian(req[ReqVariantOffset..]);

        // Everything that is about the USER — the 2329/2330 actor, the 2716 caster, the 2711 refusal —
        // uses the server's own handle, so a client cannot act as another actor.
        var casterHandle = player.EntityId;

        // A dead caster uses no item. The same lone 2711 as every other refusal (nothing consumed, no cast
        // announced), before the repeat window is read or the item looked up.
        if (player.IsDead)
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, caster is dead", player.Name, skillId);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }

        // A 1561 within the window of the caster's last ANSWERED 1561, of any item skill, gets the 2711 alone,
        // before the item is even looked up: nothing is consumed and no cast is announced (see
        // ItemSkillRepeatGuard). The refusal does not move the window.
        var now = DateTime.UtcNow;
        if (ItemSkillRepeatGuard.IsRepeat(player, now))
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, within {Window} ms of the last answered item skill",
                player.Name, skillId, ItemSkillRepeatGuard.RepeatWindow.TotalMilliseconds);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // Ownership guard: uids are global, so only the owner may consume the item. And the item must be
        // the one that carries this skill, otherwise any cheap item could
        // cast any item skill.
        var used = await inventory.GetByIdAsync(itemUid);
        if (used is null || used.CharacterId != player.CharacterId)
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, uid {Uid} is not the caster's",
                player.Name, skillId, itemUid);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }
        // Only an item in the BAG may be used. StorageType 0 is the bag; 1 is worn, 2 the bank and 3 the extra-bag
        // items themselves (slots 70..72, ClientBagSlot), none of which a consumable is used from: a worn or banked
        // uid must not be consumed straight out of its slot.
        if (used.StorageType != StorageTypeBag)
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, uid {Uid} is in storage {Storage}, not the bag",
                player.Name, skillId, itemUid, used.StorageType);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }
        var itemData = store.GetItem(used.ItemId);
        if (itemData is null || itemData.Skill_ID_1 != skillId)
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, item {ItemId} carries skill {ItemSkill}",
                player.Name, skillId, used.ItemId, itemData?.Skill_ID_1);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }

        // A warp item that moves at this answer, and where to (ItemWarpSkills). A Dungeon Wing with nowhere to go (a
        // Map_Type 0 map, or no return point) is refused with the lone 2711 before anything is consumed.
        var warpKind = ItemWarpSkills.KindOf(skillId);
        ItemWarpSkills.Destination? answerMove = warpKind == ItemWarpSkills.MoveKind.OnAnswer
            ? ItemWarpSkills.AnswerDestination(skillId, player, store)
            : null;
        if (warpKind == ItemWarpSkills.MoveKind.OnAnswer && answerMove is null)
        {
            Logger.LogDebug("{Name}: item skill {SkillId} refused, map {Map} has no return point", player.Name, skillId,
                player.MapId);
            await SendRefusalAsync(session, casterHandle, skillId);
            return;
        }

        // Answered: this request starts the repeat window for every item skill (only here, after the ownership and
        // skill checks passed).
        ItemSkillRepeatGuard.MarkAnswered(player, now);

        // An accepted item cast backs its 1509 the same way a 1508 does, so a damaging
        // item skill's 1509 is authorised and a forged one is not (SkillCastLandings; SkillDamageHandler consumes
        // it). The warp and revival item skills' 1509s are governed by the same entry before their effect
        // (SkillDamageHandler.GovernFunctionCastAsync).
        SkillCastLandings.RecordPending(player, skillId, targetHandle, now);

        // ---- 2329 OnUseItem (unicast): consume one and refresh that bag cell ------------------------
        var (bagIx, slotIx) = ClientBagSlot(used);
        var remaining = used.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(itemUid); remaining = 0; }
        else { used.Count = remaining; await inventory.UpdateItemAsync(used); }

        // ---- The item's effect (ItemInfo.Effect_ID_1): 2499 x N -> 2498 -> 2492, ahead of the 2329 --------
        // The used item's Effect_ID_1 is applied in the answer itself, FIRST: a 2492 of that effect goes out
        // before the 2329 (the run is "2499 ... 2498 2492 2329 2330 2715 2716", then the client's 1509).
        // Those 2492s name the caster as both target and caster, the request's skill as
        // the source skill, stack 1 and value 1, which is the plan ApplyItemEffectAsync builds. The effect
        // is the USED item's (resolved from the uid above), because items share skills: 10200201 is on
        // items with effects 102001 and 102003, and both are applied for it. Siege
        // statues (Item_Type 110) are skipped inside ApplyItemEffectAsync: their effect is applied after the
        // 1509 with a value no column holds. An effect with tick rows (the eggs 60001005/60001006) then
        // ticks 2495/2496 from the EffectService loop.
        if (itemData.Effect_ID_1 != 0)
        {
            var effects = services.GetRequiredService<EffectService>();
            await effects.ApplyItemEffectAsync(player, itemData);
        }

        // The used-up item counts for the "Used @1" Kharas (Complete_Type 6, the item at Value2), an Auto_Challenge one
        // being accepted on its first use, ahead of the 2329: 1561 -> 2574 -> 2577 -> 2329 -> 2330 -> 2715 -> 2716.
        await KharaService.OnItemUsedAsync(session, player, used.ItemId, services, Logger, ct);

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            writer => writer.WriteBytes(CombatPackets.BuildUseItem2329(
                actorHandle: casterHandle,
                slotIndex: slotIx,
                bagId: bagIx,
                itemUid: itemUid,
                remainingCount: (byte)Math.Clamp(remaining, 0, byte.MaxValue),
                useSource: ItemSkillUseSource)));

        // ---- 2330 OnUseItemNotify: map broadcast INCLUDING the user (BroadcastToNearby would exclude it)
        var notifyBody = LootNotifyPackets.BuildUseItemNotify2330(casterHandle, used.ItemId);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnUseItemNotify,
            writer => writer.WriteBytes(notifyBody));

        // ---- 2715 AnsUseSkillStart (unicast) --------------------------------------------------------
        // The cooldown is the table value, unreduced. That agrees with SkillStartHandler, which already sends
        // the unreduced table value for every skill id >= 2,000,000, the range all the item skills above fall in.
        store.SkillLevels.TryGetValue(skillId, out var levelData);
        var cooldown = levelData?.Cooldown_Time ?? 0f;

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsUseSkillStart,
            SkillPackets.BuildUseSkillStartAck2715(skillId, targetHandle, cooldown, posX, posY, posZ,
                variant: variant));

        // ---- The ride ends here, between the 2715 and the 2716, unless the item's skill is a riding one ---------
        // A mounted rider's answered 1561 carries its drop (2497 2498 2493 2494 2712),
        // always after "2329 2330 2715" and before the 2716. See RidingService.
        if (!RidingService.KeepsRide(store, skillId))
            await RidingService.DropAsync(session, player, services);

        // A town warp's landing grant, for this map, before the 2716 goes out: the client answers that 2716 with the
        // cast's 1509, which must find the grant (ItemWarpSkills.TakeLanding in SkillDamageHandler).
        if (warpKind == ItemWarpSkills.MoveKind.OnLanding)
            ItemWarpSkills.GrantLanding(player, skillId, player.MapId, DateTime.UtcNow);

        // ---- 2716 NfyUseSkillStart: map broadcast that INCLUDES the caster --------------------------
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyUseSkillStart,
            writer => writer.WriteBytes(SkillPackets.BuildUseSkillStart2716(
                casterHandle, skillId,
                (uint)Math.Max(0, player.Hp), (uint)Math.Max(0, player.Mp),
                targetHandle: targetHandle,
                variant: variant)));

        Logger.LogDebug("{Name} uses item {ItemId} (uid {Uid}) for skill {SkillId}, {Remaining} left",
            player.Name, used.ItemId, itemUid, skillId, remaining);

        // ---- The warp items' move (ItemWarpSkills) ---------------------------------------------------------------
        // Butterfly / Dungeon Wing: the departure right after the 2716, in this answer. A town
        // warp's 1509 moves the player once it lands (SkillDamageHandler), on the grant made before the 2716.
        if (answerMove is { } dest)
            await ItemWarpSkills.MoveAsync(session, player, dest, services, Logger, ct);
    }

    /// <summary>
    /// Refusal of a 1561: 2711 NfySkillCastingCancel for (caster, skill), unicast to the
    /// caster, and nothing else. See the remarks. Nothing is consumed and no cast is announced.
    /// </summary>
    private static Task SendRefusalAsync(ProudSession session, uint casterHandle, uint skillId) =>
        session.SendInternalRmiAsync(ZoneS2COpcodes.NfySkillCastingCancel,
            ActorStatePackets.BuildNfySkillCastingCancel(casterHandle, skillId));

    /// <summary>
    /// The client's (bag, slot) cell for an item, the same mapping WorldEntryService.WriteItemInstance
    /// uses to place it, which is the cell 2329 refreshes. Both bag 1 (slots 50-69) and extra bags
    /// (e.g. bag 3 slot 23) are covered by this partition.
    /// </summary>
    private static (byte bag, byte slot) ClientBagSlot(RO2.Inventory.Domain.InventoryItem it)
    {
        if (it.StorageType == StorageTypeEquipped)
            return (EquipmentContainer, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, MainBagFirstWireSlot - 1));
        if (it.StorageType == StorageTypeBank)
            return (BankContainer, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BankExpansion.MaxBankSlots - 1));
        if (it.StorageType == StorageTypeExtraBagItem)
            return (MainBagContainer, (byte)(ExtraBagItemFirstWireSlot + Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BagCapacity.ExtraBagCount - 1)));
        // Bag items: flat slots 0..19 are the main bag, then BagCapacity.ExtraBagStride cells per extra bag.
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < BagCapacity.MainBagCells) return (MainBagContainer, (byte)(MainBagFirstWireSlot + flat));
        var i = flat - BagCapacity.MainBagCells;
        return ((byte)Math.Clamp(FirstExtraBagContainer + i / BagCapacity.ExtraBagStride, FirstExtraBagContainer, LastExtraBagContainer),
            (byte)(i % BagCapacity.ExtraBagStride));
    }
}
