using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1322 ReqPCItemPickUp — the looter takes one item out of a corpse's loot window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire layout.</b> Body 23 bytes. The RMI argument the handler receives still carries the
/// compact-length prefix, so <c>blob+0 == payload+2</c>. Decoded blob:
/// <list type="bullet">
/// <item><c>blob+0  u16 selfLen = 21</c></item>
/// <item><c>blob+2  u16 msgTag = 0xE2A3 (58019)</c></item>
/// <item><c>blob+4  u32</c> the picker's PC actor handle (tag 0x11).</item>
/// <item><c>blob+8  u64</c> the loot uid of the item, as the corpse's 2374 listed it (entry+4).</item>
/// <item><c>blob+16 u8  pickupType/flags</c> (0 in practice).</item>
/// <item><c>blob+17 u32 (UNALIGNED)</c> the CORPSE the loot window belongs to: its low byte is the NPC tag
///   0x13, and it is the handle the preceding 1338 opened.</item>
/// </list>
/// </para>
/// <para>
/// <b>The answer</b> (sent in the same millisecond):
/// <list type="bullet">
/// <item>An item that is not the looter's last one: <c>2333</c> (result 0, kind 0, the bag record it landed
///   in), <c>2334</c> (the picker, the REQUESTED uid, the template), and <c>2362</c> when the picker is in a
///   party.</item>
/// <item>The item that leaves the looter nothing to take: the same, preceded by <c>2471</c> revoking the corpse
///   (<c>2471(0), 2333, 2334[, 2362]</c>; see CorpseLootTests), and the killer's <c>2534</c> after it for a gatherable
///   corpse once nothing is left on it at all (CorpseGatherPackets). Rolled items do not keep the corpse loaded
///   for the looter: the revoke can go out on a plain pickup while the corpse's other items are still going to the
///   dice. This server revokes once every item left is in an open roll
///   (<see cref="ItemsLooterCanStillTake"/>): a dice item the looter has not clicked yet keeps the corpse in its
///   loot list, so looting one item at a time cannot strand it. For a LootAllItems batch it is the same revoke.</item>
/// <item>A bag with no room: a lone <c>2333</c> with result 20020, kind 0 and an all-zero record, and the
///   item stays on the corpse.</item>
/// <item>A uid that is no longer there (the client re-sends a uid it has already been given):
///   nothing.</item>
/// <item>An item the party loot rule sends to the dice (<see cref="GroundItemService.CorpseItem.DiceBound"/>):
///   no pickup but a <c>2468</c> roll, opened by <see cref="PartyDiceService.OpenAsync"/>; the rule is
///   PartyDiceRolls.SendsToDice.</item>
/// <item>An item whose roll is still open: nothing.</item>
/// </list>
/// The 2333 carries the uid of the stack the item landed in, which is a held stack's uid on a merge; the 2334
/// keeps the corpse uid either way (a 1322 for a corpse uid that restacked gets a 2333 on the held
/// stack's uid and a 2334 on the corpse uid). Collect-quest ticks (2424) and Khara held-item
/// progress go out before the item's 2333, as on the auto-loot path.
/// </para>
/// <para>
/// <b>Where the items come from.</b> <see cref="GroundItemService"/>'s corpse registry, which
/// LootService.ProcessKillDropsAsync fills at every kill while <see cref="GroundItemService.CorpseLootEnabled"/>
/// is on (the default). The kill's own 2471 grant (the attack handlers, before 2392) is what lets the looter's
/// client open the corpse with 1338 (NpcShopOpenHandler answers with the 2374 list), and this handler is where
/// each listed item then goes.
/// </para>
/// <para>
/// <b>Who may pick.</b> Any of the corpse's looters (<see cref="GroundItemService.CorpseLoot.Looters"/>): the killer,
/// and in a Roll Dice, Free for All or Wearer Roll Dice party every member that was on the killer's map at the kill,
/// each granted before the 2392 (LootService.ProcessNpcCorpseAsync). See GroundItemService's remarks for the modes.
/// A member's pickup of a dice item opens its roll as the killer's does. The 2362
/// receiver is whoever picked the item up, not the killer. The pickup that empties a shared corpse revokes the
/// PICKER only. The other looters get no 2471 for it: the client drops the corpse from its
/// loot list on that 2362's count 0 (blob+32 == 0 -&gt; Remove_ActorFromLootList). Each looter still on the
/// corpse's map gets the 2534 of a gatherable corpse then, since none of them has loot left on it.
/// </para>
/// </remarks>
public class ItemPickUpHandler : ZoneRmiHandlerBase<ItemPickUpHandler>
{
    /// <summary>2333 result for "no free bag cell" (string 20020), the lone answer to a 1322 into a full bag.</summary>
    internal const ushort NoFreeBagCell = 20020;

    /// <summary>Blob offset of the u16 msgTag.</summary>
    private const int BlobMsgTagOffset = 2;

    /// <summary>Blob offset of the u32 picker actor handle.</summary>
    private const int BlobPickerHandleOffset = 4;

    /// <summary>Blob offset of the u64 loot uid.</summary>
    private const int BlobItemUidOffset = 8;

    /// <summary>Blob offset of the unaligned u32 corpse handle; a blob that reaches it holds every fixed field before it.</summary>
    private const int BlobCorpseHandleOffset = 17;

    /// <summary>Smallest blob <see cref="CheckRequest"/> reads: it must reach the loot uid at blob+8, so msgTag and picker handle are present.</summary>
    private const int BlobTagAndHandleLength = BlobItemUidOffset;

    /// <summary>Size of the u16 selfLen at blob+0.</summary>
    private const int SelfLenBytes = 2;

    /// <summary>Size of the u16 msgTag at blob+2.</summary>
    private const int MsgTagBytes = 2;

    /// <summary>Size of the u32 picker actor handle at blob+4.</summary>
    private const int PickerHandleBytes = 4;

    /// <summary>InventoryItem.StorageType of an item held in the bag.</summary>
    private const byte StorageBag = 0;

    /// <summary>InventoryItem.Durability meaning "not set": the item record writer resolves it to the template's durability.</summary>
    private const short DurabilityUnset = -1;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemPickUp;
    public override string Name => "ItemPickUp";
    public ItemPickUpHandler(IServiceScopeFactory s, ILogger<ItemPickUpHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>The 1322 fields: the loot uid (blob+8) and the corpse (blob+17). False on a short body.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> payload, out ulong itemUid, out byte pickupType, out uint corpseHandle)
    {
        itemUid = 0; pickupType = 0; corpseHandle = 0;
        if (payload.Length < 1) return false;
        // Compact-length prefix: [u8 width marker in {1,2,4,8}][width length bytes]. For 1322 the blob is 21
        // bytes so the marker is always 0x01 (one length byte), but compute it generically. Any other marker
        // counts as one length byte.
        byte marker = payload[0];
        int prefixLen = 1 + (marker == 2 ? 2 : marker == 4 ? 4 : marker == 8 ? 8 : 1);
        if (payload.Length < prefixLen + BlobCorpseHandleOffset) return false;

        var reader = new PacketReader(payload);
        reader.Skip(prefixLen);               // compact marker + length bytes
        reader.Skip(SelfLenBytes);            // blob+0  u16 selfLen
        reader.Skip(MsgTagBytes);             // blob+2  u16 msgTag (0xE2A3)
        reader.Skip(PickerHandleBytes);       // blob+4  u32 picker actor handle
        itemUid = reader.ReadUInt64();        // blob+8  (BlobItemUidOffset)
        pickupType = reader.ReadByte();       // blob+16 pickupType/flags
        corpseHandle = reader.Remaining >= sizeof(uint) ? reader.ReadUInt32() : 0u; // blob+17 (BlobCorpseHandleOffset, unaligned)
        return true;
    }

    /// <summary>msgTag the 1322 blob carries at blob+2 (0xE2A3).</summary>
    internal const ushort MsgTag = 58019;

    /// <summary>
    /// The tag and actor-handle check (INV-H12, <see cref="InventoryRequest.Validate"/>) of a 1322 body: blob+2 must
    /// be <see cref="MsgTag"/> and blob+4 the picker's own handle. A body whose blob does not reach blob+8 is
    /// <see cref="InvReject.BadMsgTag"/>. No container, slot or quantity field exists on a 1322.
    /// </summary>
    internal static InvReject CheckRequest(ReadOnlySpan<byte> payload, uint playerEntityId)
    {
        if (!payload.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobTagAndHandleLength)
            return InvReject.BadMsgTag;
        return InventoryRequest.Validate(BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(BlobMsgTagOffset)), MsgTag,
            BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(BlobPickerHandleOffset)), playerEntityId, container: 0, slot: 0, qty: 1, stackCount: 1);
    }

    /// <summary>
    /// A dead looter takes nothing (INV-H9). Only <c>IsDead</c> counts: HP 0 with the flag unset is the instant before
    /// the kill sequence sets it, and the test rigs build players with HP unset.
    /// </summary>
    internal static bool LooterMayPick(bool isDead) => !isDead;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var reject = CheckRequest(payload.Span, player.EntityId);
        if (reject != InvReject.None)
        {
            Logger.LogWarning("{Name}: 1322 rejected ({Reason}, {Len} bytes) — not answered", player.Name, reject, payload.Length);
            return;
        }
        if (!TryParse(payload.Span, out var itemUid, out var pickupType, out var corpseHandle)) return;

        if (!LooterMayPick(player.IsDead))
        {
            Logger.LogDebug("{Name}: 1322 for uid {Uid:X} while dead — not answered, the item stays", player.Name, itemUid);
            return;
        }

        Logger.LogDebug("{Name} picks up uid {Uid:X} (type {Type}, corpse {Corpse:X})",
            player.Name, itemUid, pickupType, corpseHandle);

        var npcState = services.GetRequiredService<NpcStateManager>();
        var corpse = npcState.GetNpc(corpseHandle);
        DateTime? deathStamp = corpse is { IsDead: true } ? corpse.DeathTime : null;
        // The pickup range is not enforced: the distance is only logged here, never a reason to refuse.
        if (corpse != null)
            Logger.LogDebug("{Name}: 1322 pickup distance {Dist:F1} to corpse {Corpse:X} (unenforced, range unmeasured)",
                player.Name, MathF.Sqrt(((player.X - corpse.PosX) * (player.X - corpse.PosX)) + ((player.Y - corpse.PosY) * (player.Y - corpse.PosY))), corpseHandle);
        if (!GroundItemService.TryGetCorpseLoot(player.MapId, corpseHandle, player.CharacterId, deathStamp, out var loot)
            || loot.Items.FirstOrDefault(i => i.Uid == itemUid) is not { } wanted)
        {
            // Not (or no longer) on the corpse: nothing is sent (the client may re-send a uid it had
            // already been given).
            Logger.LogDebug("{Name}: 1322 for uid {Uid:X} on corpse {Corpse:X} matched no loot — not answered",
                player.Name, itemUid, corpseHandle);
            return;
        }

        if (PartyDiceService.IsRolling(wanted.Uid))
        {
            Logger.LogDebug("{Name}: 1322 for uid {Uid:X} while its party roll is open — not answered", player.Name, itemUid);
            return;
        }

        // The party loot rule sends this item to the dice (decided at the kill): the pickup opens the roll
        // instead of taking it. A looter who has since left the party has nobody to roll with and takes it.
        var party = services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId);
        if (wanted.DiceBound && party != null)
        {
            await PartyDiceService.OpenAsync(PartyDiceService.Deps.From(services, Logger), player, party, loot, wanted);
            return;
        }

        await TakeFromCorpseAsync(session, player, services, loot, wanted, party);
    }

    /// <summary>Where one picked item goes: whole into a held stack, or into new stacks, or refused.</summary>
    /// <param name="OverPossession">With <paramref name="Refused"/>: the refusal is the item's possession limit, not a full bag.</param>
    internal sealed record Landing(InventoryItem? MergeInto, int MergedCount, IReadOnlyList<InventoryItem> NewStacks, bool Refused,
        bool OverPossession = false);

    /// <summary>
    /// Plans where <paramref name="count"/> of <paramref name="itemId"/> lands in the bag, without writing: a
    /// stackable item (Stack_Max &gt; 1) that fits WHOLE into a held stack goes there (the lowest cell of
    /// those), anything else opens new stacks of at most Stack_Max in the bag's free cells, lowest first
    /// (<see cref="BagCapacity.FreeCells"/>), and is refused when there are not enough. The same rule the
    /// kill auto-loot uses (LootService.PlanAutoLoot), for one item.
    /// An item whose bag total plus <paramref name="count"/> would pass its Possession_Max (the limit a buy is
    /// refused at, <see cref="BuyItemHandler.ExceedsPossessionLimit"/>) is refused whole, flagged
    /// <see cref="Landing.OverPossession"/>.
    /// </summary>
    internal static Landing PlanLanding(IReadOnlyList<InventoryItem> owned, uint characterId, uint itemId, int count,
        IReadOnlyDictionary<uint, ItemData> items)
    {
        if (BuyItemHandler.ExceedsPossessionLimit(owned, itemId, count, out _, out _))
            return new Landing(null, 0, Array.Empty<InventoryItem>(), true, OverPossession: true);

        items.TryGetValue(itemId, out var template);
        int perStack = Math.Max(1, (int)(template?.Stack_Max ?? 1));
        if (perStack > 1)
        {
            var held = owned
                .Where(i => i.StorageType == StorageBag && i.Slot >= 0 && i.ItemId == itemId && i.Count > 0 && i.Count + count <= perStack)
                .OrderBy(i => i.Slot)
                .FirstOrDefault();
            if (held != null)
                return new Landing(held, held.Count + count, Array.Empty<InventoryItem>(), false);
        }

        int needed = (count + perStack - 1) / perStack;
        var free = BagCapacity.FreeCells(owned, items);
        if (free.Count < needed)
            return new Landing(null, 0, Array.Empty<InventoryItem>(), true);

        short durability = template is null ? DurabilityUnset : (short)Math.Min(template.Durability, (uint)short.MaxValue);
        var stacks = new List<InventoryItem>(needed);
        int cell = 0;
        for (int remaining = count; remaining > 0;)
        {
            int n = Math.Min(perStack, remaining);
            stacks.Add(new InventoryItem
            {
                CharacterId = characterId,
                ItemId = itemId,
                Count = n,
                Slot = free[cell++],
                StorageType = StorageBag,
                Durability = durability,
            });
            remaining -= n;
        }
        return new Landing(null, 0, stacks, false);
    }

    /// <summary>
    /// How many items on the corpse the looter could still act on after a take: every item left except those in
    /// an open party roll, which PartyDiceService settles on its own. 0 is when the looter's
    /// authority is revoked (2471 granted 0), see the class remarks.
    /// </summary>
    internal static int ItemsLooterCanStillTake(uint mapId, uint corpseHandle, uint looterCharId, DateTime? deathStamp)
        => GroundItemService.TryGetCorpseLoot(mapId, corpseHandle, looterCharId, deathStamp, out var rest)
            ? rest.Items.Count(i => !PartyDiceService.IsRolling(i.Uid))
            : 0;

    private async Task TakeFromCorpseAsync(ProudSession session, PlayerState player, IServiceProvider services,
        GroundItemService.CorpseLoot loot, GroundItemService.CorpseItem wanted, PartyState? party)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        var landing = PlanLanding(owned, player.CharacterId, wanted.ItemTemplateId, wanted.Count, store.Items);

        if (landing.OverPossession)
        {
            // No answer is defined for a possession refusal, and 20020 would say "bag full" for an
            // item that has room. Nothing is sent and the item stays on the corpse.
            Logger.LogInformation("{Name}: {Count}x item {Id} would pass its possession limit, stays on corpse {Corpse:X}",
                player.Name, wanted.Count, wanted.ItemTemplateId, loot.CorpseHandle);
            return;
        }

        if (landing.Refused)
        {
            // Full bag: the item stays on the corpse.
            await SendBagFullAsync(session);
            Logger.LogInformation("{Name}: bag full, {Count}x item {Id} stays on corpse {Corpse:X}",
                player.Name, wanted.Count, wanted.ItemTemplateId, loot.CorpseHandle);
            return;
        }

        if (!GroundItemService.TryTakeCorpseItem(loot.MapId, loot.CorpseHandle, wanted.Uid, player.CharacterId,
                loot.DeathStamp, out var item, out var itemsLeft))
            return; // a racing request took it first

        var landed = await WriteLandingAsync(inventory, landing, player, item.ItemTemplateId, Logger);
        if (landed.Count == 0)
        {
            // Nothing reached the bag: the item goes back on the corpse rather than vanish.
            GroundItemService.ReturnCorpseItem(loot.MapId, loot.CorpseHandle, item);
            return;
        }

        // The pickup that leaves the looter nothing more to take revokes the corpse first (see the class remarks),
        // for the picker alone: a shared corpse's other looters drop it on the 2362 below, whose count is then 0.
        if (ItemsLooterCanStillTake(loot.MapId, loot.CorpseHandle, player.CharacterId, loot.DeathStamp) == 0)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                CombatPackets.BuildLootingAuthority2471(loot.CorpseHandle, granted: false));

        await NotifyLandedAsync(session, player, services, Broadcast, Logger, landed, item.Uid, item.ItemTemplateId, item.Count);

        // Party loot log: the corpse uid (the dropped instance, not the stack it merged into), the corpse, and
        // the items still on it — 0 on the last one, which is what takes the corpse out of members' lists.
        if (party != null)
        {
            var partyBody = PartyLootNotifyPackets.BuildPartyNotifyGetItem2362(PartyAnswerPackets.MemberGid(player),
                item.Uid, loot.CorpseHandle, item.ItemTemplateId, trailing: (byte)Math.Clamp(itemsLeft, 0, byte.MaxValue));
            await services.GetRequiredService<PartyService>()
                .BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyGetItem, partyBody);
        }

        // The looters' 2534 for a gatherable corpse, once its loot is gone: the looter sequence is revoke
        // 2471 -> last 2333 + 2334 -> 2534 (the same rule
        // LootService applies to a corpse that holds nothing at the kill), the picker first. A shared corpse's
        // other looters have no loot left on it either, so each one still on the corpse's map gets its own (the
        // 2534 marks the corpse gatherable on a client once that client has nothing left to loot on it). The corpse
        // itself stays: its 2507 is the kill's detached despawn, which GroundItemService.WaitUntilCorpseMayLeaveAsync
        // releases Corpse_Disappear_Time after this pickup (TryTakeCorpseItem recorded when it emptied the corpse),
        // or sooner if the NPC's respawn comes due first.
        if (itemsLeft == 0 && (store.Npcs.GetValueOrDefault(loot.NpcId)?.AfterDeath_FuncObject_ID ?? 0) != 0)
        {
            var gather = CorpseGatherPackets.BuildNotifyCanGatherDeadNpc2534(loot.CorpseHandle);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc, gather);
            foreach (var looter in loot.Looters)
            {
                if (looter.CharId == player.CharacterId) continue;
                if (PlayerManager.GetPlayerByCharacterId(looter.CharId) is not { } other || other.MapId != loot.MapId)
                    continue;
                if (Broadcast.GetSession(other.SessionId) is { } otherSession)
                    await otherSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc, gather);
            }
        }

        Logger.LogInformation("{Name} looted {Count}x item {Item} from corpse {Corpse:X} ({Left} left)",
            player.Name, item.Count, item.ItemTemplateId, loot.CorpseHandle, itemsLeft);
    }

    /// <summary>The lone answer to a pickup into a full bag: 2333 [20020][kind 0][126 zero bytes].</summary>
    internal static Task SendBagFullAsync(ProudSession session)
        => session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
            LootNotifyPackets.BuildPickupItem2333(new byte[LootNotifyPackets.ItemRecordLength], resultCode: NoFreeBagCell));

    /// <summary>
    /// Writes a planned landing to the bag and returns the stacks that reached it, as they now stand. Empty
    /// when nothing did; the caller then puts the item back where it came from rather than let it vanish.
    /// A write that fails part-way returns the stacks written before it. Shared with the dice award
    /// (PartyDiceService), which lands a rolled item in its winner's bag.
    /// </summary>
    internal static async Task<List<InventoryItem>> WriteLandingAsync(IInventoryRepository inventory, Landing landing,
        PlayerState player, uint itemTemplateId, ILogger logger)
    {
        var landed = new List<InventoryItem>(1);
        try
        {
            if (landing.MergeInto is { } held)
            {
                held.Count = landing.MergedCount;
                await inventory.UpdateItemAsync(held);
                landed.Add(held);
            }
            foreach (var stack in landing.NewStacks)
            {
                await inventory.AddItemAsync(stack); // assigns stack.Id, the bag uid the 2333 carries
                landed.Add(stack);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: saving picked item {Id} failed ({Landed} stack(s) written)",
                player.Name, itemTemplateId, landed.Count);
        }
        return landed;
    }

    /// <summary>
    /// What every successful pickup sends once its item is in the bag: quest and Khara progress, the 2333 of
    /// each stack it landed in, then the source-inclusive 2334 naming the uid the pickup asked for. The dice
    /// award sends the same to its winner.
    /// </summary>
    internal static async Task NotifyLandedAsync(ProudSession session, PlayerState player, IServiceProvider services,
        BroadcastService broadcast, ILogger logger, IReadOnlyList<InventoryItem> landed, ulong lootUid,
        uint itemTemplateId, int count)
    {
        // Quest and Khara progress for the new bag contents, ahead of the item's 2333 (the kill path
        // uses the same order: 2424 before the 2333 of the same item).
        await services.GetRequiredService<LootService>()
            .AdvanceCollectQuestsAsync(session, player, itemTemplateId, count);
        await KharaService.OnHeldItemChangedAsync(session, player, itemTemplateId, services, logger);

        foreach (var stack in landed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)));

        // Source-inclusive: the picker's own client strikes the entry from its loot window with it.
        var notify = PickupNotifyPackets.BuildPickupItemNotify2334(player.EntityId, lootUid, itemTemplateId);
        await broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPickupItemNotify,
            writer => writer.WriteBytes(notify));

        // A card new to the character's book (or an MVP card) is announced with 2588 once it is in the bag,
        // right after the 2333/2334 of that card. The dice award reaches the bag through here too.
        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { itemTemplateId },
            services, logger);

        // Ranking type 4 (Collector) counts the card rows this landing opened; the corpse pickup and the dice
        // award are where a kill's card reaches the bag while corpse loot is on (RankingService.CountCardLandingAsync).
        await RankingService.CountCardLandingAsync(services, player, itemTemplateId, count, landed, logger);
    }
}
