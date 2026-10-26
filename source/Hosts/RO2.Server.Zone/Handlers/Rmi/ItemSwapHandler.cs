using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1324 ReqPCItemSwap — move/rearrange/equip an item. Blob (after the compact marker):
///   selfLen u16 @0, msgTag 0xE2A8 @2, playerHandle u32 @4,
///   SRC descriptor @8:  [u8 slot][u8 bag][u64 uid][u8 flag]  (11 bytes),
///   DST descriptor @19: [u8 slot][u8 bag][u64 uid][u8 flag]  (11 bytes).
/// slot/bag are the CLIENT coordinates (bag 1 slot 0-49 = equipment window, bag 1 slot 50-69 = main
/// bag, bags 2-4 slot 0-34 = extra bags); uid is the item instance id we stamped into the 126-byte
/// record. The server swaps the two items' positions and answers OnSwapItem (2327) with the records
/// at their NEW positions so the client redraws both cells.
/// </summary>
public class ItemSwapHandler : ZoneRmiHandlerBase<ItemSwapHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemSwap;
    public override string Name => "ItemSwap";

    /// <summary>The 1324 message tag, blob+2 (0xE2A8).</summary>
    internal const ushort MsgTag = 58024;

    // Request blob layout (see the class summary). Each descriptor is [u8 slot][u8 bag][u64 uid][u8 qty].
    private const int BlobMsgTagOffset = 2;
    private const int BlobHandleOffset = 4;
    private const int SrcDescriptorOffset = 8;
    private const int DstDescriptorOffset = 19;
    private const int DescriptorLength = 11;
    private const int DescriptorBagOffset = 1;
    private const int DescriptorUidOffset = 2;
    private const int DescriptorQtyOffset = 10;

    /// <summary>The shortest blob that still reaches the whole DST descriptor (blob+19 + 11 bytes).</summary>
    private const int MinBlobLength = DstDescriptorOffset + DescriptorLength;

    // StorageType values of InventoryItem.
    private const byte StorageBag = 0;          // main bag and extra bags
    private const byte StorageEquipped = 1;     // equipment window
    private const byte StorageBagWindow = 3;    // character bag window (the equipped bags)

    // Client coordinates: bag 1 slot 0-49 is the equipment window, slot 50-69 the main bag, slot 70-72 the bag window;
    // bags 2-4 hold the extra bags, one container per equipped bag.
    private const int EquipmentWindowSlots = 50;
    private const int MainBagFirstSlot = EquipmentWindowSlots;
    private const int BagWindowFirstSlot = MainBagFirstSlot + BagCapacity.MainBagCells;
    private const byte LastCharacterWindowBag = 1;
    private const int FirstExtraBagContainer = 2;

    // Item record (126 bytes, ItemPackets.ItemRecordLength) field offsets: uid u64 @0, template id u32 @8, stack count u8 @14.
    private const int RecordItemIdOffset = 8;
    private const int RecordCountOffset = 14;

    /// <summary>Uid stamped on the clear record of a plain move: non-zero, and unmatched by any real item uid.</summary>
    private const ulong ClearRecordUid = ulong.MaxValue;

    /// <summary>Slot of a row parked outside the unique placement index (only Slot &gt;= 0 is indexed).</summary>
    private const short UnplacedSlot = -1;

    /// <summary>The 2327 result code of a success (a revert is sent as an all-zero primary and vector).</summary>
    private const ushort SwapResultSuccess = 0;

    /// <summary>The 2327 vector always carries exactly one record.</summary>
    private const ushort SwapVectorCount = 1;
    public ItemSwapHandler(IServiceScopeFactory s, ILogger<ItemSwapHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        byte srcSlot, srcBag, dstSlot, dstBag, srcQty;
        ulong srcUid, dstUid;
        ushort msgTag;
        uint handle;
        {
            // Lenient: the declared length is not compared, only that the blob reaches the DST uid.
            if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < MinBlobLength) return;
            // Each descriptor is [u8 slot][u8 bag][u64 uid][u8 qty]. The SRC qty byte (blob+18) is the amount
            // of the stack to move: == the whole stack for a plain move/merge, but LESS for a split (dropping
            // part of a stack into an empty cell). Ignoring it made every split move the entire stack.
            msgTag = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(BlobMsgTagOffset));
            handle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(BlobHandleOffset));
            srcSlot = blob[SrcDescriptorOffset]; srcBag = blob[SrcDescriptorOffset + DescriptorBagOffset];
            srcUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(SrcDescriptorOffset + DescriptorUidOffset));
            srcQty = blob[SrcDescriptorOffset + DescriptorQtyOffset];
            dstSlot = blob[DstDescriptorOffset]; dstBag = blob[DstDescriptorOffset + DescriptorBagOffset];
            dstUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(DstDescriptorOffset + DescriptorUidOffset));
        }

        // INV-H6: an item cannot be swapped with, or merged into, itself (the merge would load the same document twice
        // and lose a unit or delete the stack). INV-H12: tag, actor handle and the destination coordinates are checked
        // before any repository read.
        if (srcUid == dstUid ||
            InventoryRequest.Validate(msgTag, MsgTag, handle, player.EntityId, dstBag, dstSlot, qty: 1, stackCount: 1) != InvReject.None)
        {
            await SendRevert(session);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        // Serialise the whole read-decide-write per character: two concurrent 1324s for one character would
        // otherwise interleave their reads and writes (lost update, or two rows transiently on one cell, which
        // the unique placement index now rejects). Different characters never wait on each other. Not re-entrant:
        // nothing inside takes this character's gate again (the split's AddItemAsync carries a real Slot>=0, so it
        // inserts directly without re-gating).
        await CharacterInventoryGate.RunAsync(player.CharacterId, async () =>
        {
        var srcItem = await inventory.GetByIdAsync(srcUid);
        // Ownership guard: uids are global, so both the moved item and any swap partner must belong to
        // THIS player — otherwise a client could relocate/steal another player's item by uid.
        if (srcItem is null || srcItem.CharacterId != player.CharacterId)
        {
            // Unknown/foreign source (e.g. a client-side phantom): revert the drag safely instead of crashing.
            await SendRevert(session);
            return 0;
        }
        var dstItem = dstUid != 0 ? await inventory.GetByIdAsync(dstUid) : null;
        if (dstItem is not null && dstItem.CharacterId != player.CharacterId)
        {
            await SendRevert(session);
            return 0;
        }

        // INV-H12: quantity against the stored stack (the tag, handle and destination were checked above).
        if (InventoryRequest.Validate(msgTag, MsgTag, handle, player.EntityId, srcBag, srcSlot, srcQty, srcItem.Count) != InvReject.None)
        {
            await SendRevert(session);
            return 0;
        }

        // INV-H5/H6: the client's claimed cells must match the stored rows (the source is derived from the stored row,
        // the destination must hold the claimed partner or be an empty cell that exists). One read of the character's
        // rows serves this check and the equip gates below; nothing writes before they run.
        var gameData = services.GetRequiredService<GameDataStore>();
        var allRows = await inventory.GetByCharacterAsync(player.CharacterId);
        var claim = CheckClaims(srcItem, srcBag, srcSlot, dstItem, dstUid, dstBag, dstSlot, allRows, gameData.GetItem);
        if (claim != SwapRefusal.None)
        {
            Logger.LogWarning("{Name} 1324 refused: {Reason} (src uid {Src} bag {SB}/slot {SS}, dst uid {Dst} bag {DB}/slot {DS})",
                player.Name, claim, srcUid, srcBag, srcSlot, dstUid, dstBag, dstSlot);
            await SendRevert(session);
            return 0;
        }

        // Is the source a stackable, non-equipped item? (Stack_Max > 1.) Stacking never applies to gear.
        gameData.Items.TryGetValue(srcItem.ItemId, out var sdata);
        int srcMax = sdata?.Stack_Max ?? 0;
        var srcStackable = srcItem.StorageType != StorageEquipped && srcMax > 1;

        // STACK MERGE: dropping (part of) a stack onto another stack of the SAME stackable item combines them,
        // up to Stack_Max, with any overflow left in the source. The 2327 answer carries
        // PRIMARY = SOURCE cell (its remaining count, or a count-0 clear
        // when emptied) and VECTOR = DEST cell (its new, capped count); neither item changes cell.
        // INV-H6: both stacks must sit in the same kind of container (a bag stack never merges into another container).
        if (dstItem is not null && srcStackable && dstItem.ItemId == srcItem.ItemId && dstItem.StorageType != StorageEquipped
            && dstItem.StorageType == srcItem.StorageType)
        {
            int max = srcMax;
            int qty = Math.Clamp((int)srcQty, 1, srcItem.Count);       // how much the client asked to move
            int dstNew = Math.Min(max, dstItem.Count + qty);
            int moved = dstNew - dstItem.Count;                        // how much actually fit
            int srcNew = srcItem.Count - moved;                        // remainder left in the source
            dstItem.Count = dstNew;
            await inventory.UpdateItemAsync(dstItem);
            var dstRec = ItemPackets.BuildItemRecord126(dstItem); // dest cell, new capped count
            byte[] srcRec;
            if (srcNew <= 0)
            {
                await inventory.RemoveItemAsync(srcItem.Id);           // source fully absorbed
                srcRec = ItemPackets.BuildItemRecord126(srcItem);
                srcRec[RecordCountOffset] = 0;                                        // count 0 -> clear the source cell
            }
            else
            {
                srcItem.Count = srcNew;
                await inventory.UpdateItemAsync(srcItem);
                srcRec = ItemPackets.BuildItemRecord126(srcItem);
            }
            await SendSwap2327(session, srcRec, dstRec);
            Logger.LogInformation("{Name} merged {Moved} of uid {Src} into {Dst}: dst {Dc}, src {Sc} (max {Max})",
                player.Name, moved, srcUid, dstUid, dstNew, srcNew, max);
            return 0;
        }

        // STACK SPLIT: dropping PART of a stack into an EMPTY cell creates a new stack there and shrinks the
        // source. The client signals this with dstUid == 0 (empty target) and a SRC qty below the whole stack.
        if (dstItem is null && srcStackable && srcQty >= 1 && srcQty < srcItem.Count)
        {
            int qty = srcQty;
            var (nflat, nstorage) = ClientToPos(dstBag, dstSlot);
            // A split that lands in a worn slot (a card off a stack into a card cell) is an equip placement: enforce
            // the same legality as a swap before creating the row. vacatingUid 0 (the target cell was empty — this
            // branch runs only when dstUid == 0). Overlap enforcement OFF (see EquipRules).
            if (nstorage is StorageEquipped or StorageBagWindow &&
                EquipRules.CanPlace(srcItem, sdata, nstorage, nflat,
                    new EquipActor(player.Level, player.Job, player.Gender), allRows) != EquipRefusal.None)
            {
                await SendRevert(session);
                return 0;
            }
            var newItem = new RO2.Inventory.Domain.InventoryItem
            {
                CharacterId = player.CharacterId,
                ItemId = srcItem.ItemId,
                Count = qty,
                Slot = (short)nflat,
                StorageType = nstorage,
            }.CopyInstanceStateFrom(srcItem); // the split-off part keeps the source's per-instance state
            // A split into the equipment window (one card of a stack into a card slot) wears the new row.
            BindOnWear(newItem, sdata?.Bind_Type ?? 0);
            await inventory.AddItemAsync(newItem);                     // assigns a fresh uid
            srcItem.Count -= qty;
            await inventory.UpdateItemAsync(srcItem);
            var srcRec = ItemPackets.BuildItemRecord126(srcItem); // source cell, reduced count
            var newRec = ItemPackets.BuildItemRecord126(newItem); // dest cell, split-off count
            await SendSwap2327(session, srcRec, newRec);
            Logger.LogInformation("{Name} split {Qty} of uid {Src} into new uid {New} at bag {Bag}/slot {Slot}",
                player.Name, qty, srcUid, newItem.Id, dstBag, dstSlot);
            return 0;
        }

        // Move the source item to the destination position; swap the dest item back to the source.
        var (srcFlat, srcStorage) = ClientToPos(srcBag, srcSlot);
        var (dstFlat, dstStorage) = ClientToPos(dstBag, dstSlot);

        // EQUIP-LEGALITY (SP0-EQUIP wiring): a swap/move that PLACES an item into a worn slot (StorageType 1) must
        // satisfy the slot map and the item's Require_Level/Job/Sex and item/equip-type gates — ownership alone let
        // a client equip ANY owned item in ANY slot (stat inflation). Validate BEFORE touching the DB, against the
        // inventory as it stands. The moved source lands at (dstFlat, dstStorage); its swap partner (dstItem) vacates
        // that cell, so pass dstUid as vacatingUid so the partner's cell is not counted occupied. In a worn<->worn
        // swap, or when a worn item is unequipped into the cell a bag item vacates, the partner also lands worn (at
        // srcFlat): validate it too, with srcUid vacating. Overlap (Equip_Slot_Overlap) enforcement is OFF — its
        // semantics are unverified (see EquipRules). A bag<->bag move (no worn target) skips this entirely.
        // INV-H4: the same gate runs for the character bag window (StorageType 3), where CanPlace enforces Item_Type 7
        // (Bag_Size is also set on Item_Type 39/129), the bag cell 0..2 and the occupancy; and a bag that leaves or is
        // replaced in that window must not strand the items still in its cells (BagWindowWouldStrandItems).
        if (dstStorage is StorageEquipped or StorageBagWindow || srcStorage is StorageEquipped or StorageBagWindow)
        {
            var actor = new EquipActor(player.Level, player.Job, player.Gender);
            if (dstStorage is StorageEquipped or StorageBagWindow &&
                EquipRules.CanPlace(srcItem, sdata, dstStorage, dstFlat, actor, allRows, dstUid) != EquipRefusal.None)
            {
                await SendRevert(session);
                return 0;
            }
            if (srcStorage is StorageEquipped or StorageBagWindow && dstItem is not null &&
                EquipRules.CanPlace(dstItem, gameData.GetItem(dstItem.ItemId), srcStorage, srcFlat, actor, allRows, srcUid) != EquipRefusal.None)
            {
                await SendRevert(session);
                return 0;
            }
            if (BagWindowWouldStrandItems(srcItem, srcFlat, srcStorage, dstItem, dstFlat, dstStorage, allRows, gameData.GetItem))
            {
                Logger.LogWarning("{Name} 1324 refused: the bag window cell still holds items (src uid {Src}, dst uid {Dst})",
                    player.Name, srcUid, dstUid);
                await SendRevert(session);
                return 0;
            }
        }

        // Build the OLD-cell CLEAR record from the source item BEFORE we move it: a record at the source's
        // CURRENT (bag, slot) with the stack count forced to 0 makes the client empty that cell
        // (UIManager_UpdateBagSlot clears a cell when record+0x0E == 0). This is what stops the unequip
        // DUP: a plain move (drag to an empty cell) has no swap partner to overwrite the source cell, and
        // the client's by-uid auto-remove does not reliably fire for gear we delivered outside the normal
        // item-list path — so without an explicit clear the paperdoll keeps the item while the bag also
        // shows it. (Equip/unequip are 1324 ReqPCItemSwap, msgTag 0xE2A8; the client
        // sent the same uid at two positions after an unequip, i.e. its own state had duplicated.)
        var clearOld = ItemPackets.BuildItemRecord126(srcItem);
        clearOld[RecordCountOffset] = 0;                       // count 0 -> UpdateBagSlot clears the cell
        // The clear record's UID (record[0..7]) must be NON-ZERO but must NOT match any equipped item.
        // The 2327 consumer runs each record through the bag-UI updater and the model dresser. The bag-UI updater
        // returns early when the record's UID is zero, so UID 0 fails to clear the old cell
        // and the item DUPLICATES: it stays in the bag AND gets equipped. The model dresser, on its bag!=0 branch,
        // looks the UID up and UNDRESSES the just-equipped
        // item if the UID is a live equipped one. A sentinel UID satisfies both: non-zero so the bag cell
        // clears, and unmatched so no equipped item is undressed. (Real item UIDs are small DB ids.)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(clearOld.AsSpan(0), ClearRecordUid);

        // Bag-window (StorageType 3) CLEAR record for the 2327 PRIMARY: a normal record at the SOURCE (bag, slot)
        // with the item TEMPLATE id (record[8..11]) and stack count (record[14]) zeroed, but the REAL uid kept.
        // That empties the source bag-window cell: NOT a sentinel uid, and NO separate 2333.
        // Built before the move so it carries the OLD source cell.
        var bagClearSource = ItemPackets.BuildItemRecord126(srcItem);
        Array.Clear(bagClearSource, RecordItemIdOffset, sizeof(uint));                       // template id = 0
        bagClearSource[RecordCountOffset] = 0;                                                              // stack count = 0

        // Wearing a bind-on-equip item binds it for good (InventoryItem.Bound): set before the row is stored and
        // before the 2327 records below are built, so the record that lands it in the equipment window already
        // carries the bit. Checked before the move as well, so a row that was already worn when the
        // history started being kept is bound as it comes off.
        var srcBindType = sdata?.Bind_Type ?? 0;
        BindOnWear(srcItem, srcBindType);
        srcItem.Slot = (short)dstFlat;
        srcItem.StorageType = dstStorage;
        BindOnWear(srcItem, srcBindType);
        if (dstItem is not null)
        {
            var dstBindType = gameData.GetItem(dstItem.ItemId)?.Bind_Type ?? 0;
            BindOnWear(dstItem, dstBindType);
            dstItem.Slot = (short)srcFlat;
            dstItem.StorageType = srcStorage;
            BindOnWear(dstItem, dstBindType);

            // Index-safe write order. The unique placement index is partial (only Slot>=0), so a swap must never
            // leave two rows on one (CharacterId, StorageType, Slot). Park the destination row at the exempt
            // unplaced slot (-1), move the source into the now-free destination cell, then settle the destination
            // row into the vacated source cell: three ordered updates with no intermediate collision. The final
            // slots and the 2327 answer built below are byte-identical to a plain two-write swap — only the DB
            // write sequence changes.
            var dstFinalSlot = dstItem.Slot;
            dstItem.Slot = UnplacedSlot;
            await inventory.UpdateItemAsync(dstItem);   // (1) park destination at the exempt unplaced slot
            await inventory.UpdateItemAsync(srcItem);   // (2) source -> freed destination cell
            dstItem.Slot = dstFinalSlot;
            await inventory.UpdateItemAsync(dstItem);   // (3) destination -> freed source cell
        }
        else
        {
            await inventory.UpdateItemAsync(srcItem);   // plain move into an empty cell: one write, no collision
        }

        Logger.LogInformation("{Name} moved uid {Uid} to bag {Bag}/slot {Slot} (swap uid {DstUid})",
            player.Name, srcUid, dstBag, dstSlot, dstUid);

        // OnSwapItem 2327 — raw u32-length VarBlob32 { u16 result, 126B primary, VecU16<126B> }. The
        // client places the primary and each vector record at the (bag, slot) each record carries.
        //
        // RECORD CONVENTION:
        //   PRIMARY   = what is NOW at the SOURCE cell  (a count-0/tid-0 CLEAR for a plain move, or the
        //               swap partner now sitting in the source cell for a real swap),
        //   VECTOR[0] = the MOVED item at its DESTINATION cell (count 1).
        // The character bag window (StorageType 3) refreshes its slot ONLY when the swap is framed this way,
        // with no separate 2333 OnPickupItem for a bag move. Gear/normal moves use the
        // moved-item-as-PRIMARY framing, which the paperdoll path handles correctly on screen.
        byte[] primary, secondary;
        if (srcStorage == StorageBagWindow || dstStorage == StorageBagWindow)
        {
            primary = dstItem is not null
                ? ItemPackets.BuildItemRecord126(dstItem)   // swap: partner now at the source cell
                : bagClearSource;                                 // plain move: source cell cleared (real uid, tid 0, count 0)
            secondary = ItemPackets.BuildItemRecord126(srcItem); // moved item at its destination cell
        }
        else
        {
            primary = ItemPackets.BuildItemRecord126(srcItem);
            secondary = dstItem is not null
                ? ItemPackets.BuildItemRecord126(dstItem)
                : clearOld;
        }
        await session.SendRmiAsync(ZoneS2COpcodes.OnSwapItem, writer =>
        {
            var lenPos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(SwapResultSuccess);
            writer.WriteBytes(primary);
            writer.WriteUInt16(SwapVectorCount);
            writer.WriteBytes(secondary);
            writer.WriteAt(lenPos, (uint)(writer.Length - start));
        });

        // Equipment changed -> refresh the OWNER's stat sheet AND show the new gear to OTHERS.
        // Fetch the worn set once and use it for both.
        if (srcStorage == StorageEquipped || dstStorage == StorageEquipped)
        {
            var worn = await inventory.GetEquipmentAsync(player.CharacterId);

            // Owner: re-send the FULL 2498 OnPcTrait so the sheet's secondary stats (attack, defense, hit,
            // crit, …) reflect the new worn set. Those are 2498-driven (proved on-screen), so an
            // equip/unequip needs a fresh 2498 or the sheet stays stale. The full builder re-stamps the
            // primaries too, so their slots are not zeroed; the primaries come from the char's persisted
            // stats (equip doesn't change them). Current HP/SP (blob+72/+76, which the client applies as its
            // current bars) are the LIVE PlayerState values, clamped to the new geared max as the lines below
            // clamp the server's: the world-entry snapshot keeps its login Hp/Mp all session, so building
            // from it repainted the bars to the login value on every swap.
            // Equipping/removing gear changes the effective primaries: STR/INT drive the damage the combat path
            // deals, VIT drives MaxHP + DEF, WIS drives MaxSP. EffectService.RefreshPcTraitSheet builds the sheet
            // with the effects still active folded in (every 2498 restates the buffed sheet, see there),
            // then sets the runtime max to it (or HP snaps back on the next hit), clamps current down if the max
            // shrank (a larger max just adds headroom, no heal) and caches the same stats for combat.
            if (player.Character is { } ch)
            {
                var store = services.GetRequiredService<GameDataStore>();
                var trait = services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, trait);
                // Cache the newly worn weapon's damage range for the combat formula (recompute rule).
                (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, worn);
            }

            // Nearby players: broadcast the FULL 2848 NfyPCEquipView snapshot so observers see the new
            // gear. 2848 is a SNAPSHOT, not a delta — sending the complete worn set keeps exactly those
            // slots visible on the remote actor (an empty set legitimately means "wearing nothing"); the
            // safe crash-checked builder frames the verified 28-byte records. See EquipViewNotifyPackets.
            // The PC's 2315 record follows it.
            await EquipViewNotifyPackets.BroadcastEquipChangeAsync(Broadcast, player, worn, services.GetRequiredService<IGuildEmblemRepository>());
        }

        // NOTE: no 2333 OnPickupItem here. The bag-window cell is refreshed by the 2327
        // framing above (PRIMARY = source clear, VECTOR = moved item); a bag equip/unequip needs
        // exactly one 2327 and no 2333. (Inventory capacity expansion via 2378 stays parked — see SendInvenSizeAsync.)
        return 0;
        });
    }

    /// <summary>Base inventory count for 2378 OnUpdateInvenSize: the client's InitializeSlots treats the value
    /// as (equipment offset 50 + main-bag 20); each equipped bag adds its Bag_Size on top.</summary>
    private const int BaseInvenCount = MainBagFirstSlot + BagCapacity.MainBagCells;

    /// <summary>Recompute total inventory slot count (base + Σ Bag_Size of the equipped bags in the character
    /// bag window, StorageType 3) and send 2378 OnUpdateInvenSize so the client grid resizes.</summary>
    internal static async Task SendInvenSizeAsync(ProudSession session, PlayerState player, IInventoryRepository inventory, IServiceProvider services, bool onlyIfBags = false)
    {
        // DISABLED: sending 2378 OnUpdateInvenSize made the client's UIBagWnd::InitializeSlots
        // REBUILD the inventory grid, which dropped the existing main-bag items ("cannot use the basic bag").
        // The count scaling and whether the items must be re-sent after the resize are still unresolved.
        // The bag itself still equips and shows in the bag window via the StorageType-3
        // container mapping; only the capacity expansion is parked until the resize flow is understood.
        await Task.CompletedTask;
        return;
#pragma warning disable CS0162 // unreachable while disabled
        var store = services.GetRequiredService<GameDataStore>();
        var all = await inventory.GetByCharacterAsync(player.CharacterId);
        int extra = 0;
        foreach (var it in all)
            if (it.StorageType == StorageBagWindow && store.Items.TryGetValue(it.ItemId, out var bi)) extra += bi.Bag_Size;
        // Safety: on world entry (onlyIfBags), do NOT touch the grid for players with no equipped bags — the
        // client's default sizing already holds their items, and the 2378 count scaling is still unresolved.
        if (onlyIfBags && extra == 0) return;
        int count = BaseInvenCount + extra;
        await session.SendRmiAsync(ZoneS2COpcodes.OnUpdateInvenSize,
            w => w.WriteBytes(ShopItemPackets.BuildUpdateInvenSize2378(player.EntityId, count)));
    }

    /// <summary>OnSwapItem 2327 with PRIMARY at the source cell and one VECTOR record at the dest cell —
    /// the framing used for merges and splits (result = success).</summary>
    private static async Task SendSwap2327(ProudSession session, byte[] primary, byte[] secondary) =>
        await session.SendRmiAsync(ZoneS2COpcodes.OnSwapItem, writer =>
        {
            var lenPos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(SwapResultSuccess);
            writer.WriteBytes(primary);
            writer.WriteUInt16(SwapVectorCount);
            writer.WriteBytes(secondary);
            writer.WriteAt(lenPos, (uint)(writer.Length - start));
        });

    private static async Task SendRevert(ProudSession session) =>
        await session.SendRmiAsync(ZoneS2COpcodes.OnSwapItem, writer =>
        {
            var lenPos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(SwapResultSuccess);
            writer.WriteZeros(ItemPackets.ItemRecordLength);   // uid-0 records are safe (the client's model dresser ignores them)
            writer.WriteUInt16(SwapVectorCount);
            writer.WriteZeros(ItemPackets.ItemRecordLength);
            writer.WriteAt(lenPos, (uint)(writer.Length - start));
        });

    /// <summary>ItemInfo.Bind_Type of the bind-on-equip templates (2 binds on acquisition and needs no history).</summary>
    internal const byte BindOnEquip = 1;

    /// <summary>
    /// Marks <paramref name="item"/> bound (InventoryItem.Bound) when it now sits in the equipment window
    /// (StorageType 1) or the character bag window (StorageType 3) and its template binds on equip. Returns true
    /// when the flag changed.
    /// </summary>
    /// <remarks>
    /// The 2327 that moves a Bind_Type-1 card into bag 0 slots 20-24 after the
    /// 1324 that equips it carries record+0x1B = 2, and every later bag record of
    /// that uid keeps it; see InventoryItem.Bound. Equipping a
    /// Bind_Type-1 bag binds it the same way: the 2327 that puts a bag in
    /// bag 1 slots 70-72 carries the bit. 1324 is the only request that
    /// puts an item in either window here, so this is the one place the history is written.
    /// </remarks>
    internal static bool BindOnWear(InventoryItem item, byte bindType)
    {
        if (item.StorageType is not (StorageEquipped or StorageBagWindow) || bindType != BindOnEquip || item.Bound) return false;
        item.Bound = true;
        return true;
    }

    /// <summary>
    /// Reverse of WriteItemInstance's (StorageType, flat Slot) -> (bag, slot) mapping. bag 1 slot 0-49
    /// is the equipment/character window (StorageType 1); bag 1 slot 50-69 is the main bag (flat 0-19);
    /// bags 2-4 slot 0-34 are extra bags (flat 20+).
    /// </summary>
    internal static (int flat, byte storage) ClientToPos(byte bag, byte slot)
    {
        if (bag <= LastCharacterWindowBag && slot < EquipmentWindowSlots) return (slot, StorageEquipped);                       // equipment
        if (bag <= LastCharacterWindowBag && slot >= BagWindowFirstSlot) return (slot - BagWindowFirstSlot, StorageBagWindow); // character bag window (equipped bags 0-2)
        if (bag <= LastCharacterWindowBag) return (slot - MainBagFirstSlot, StorageBag);                                       // main bag (slots 50-69)
        return (BagCapacity.MainBagCells + (bag - FirstExtraBagContainer) * BagCapacity.ExtraBagStride + slot, StorageBag);    // extra bags
    }

    /// <summary>Why <see cref="CheckClaims"/> refused a 1324.</summary>
    internal enum SwapRefusal
    {
        None,
        /// <summary>The source and the destination are the same item.</summary>
        SameItem,
        /// <summary>The (bag, slot) the client claims for the source is not where the stored row is.</summary>
        SourceNotWhereClaimed,
        /// <summary>The destination uid is not at the claimed destination cell (or does not exist).</summary>
        DestinationMismatch,
        /// <summary>The destination uid is 0 (an empty cell) but another row holds that cell.</summary>
        DestinationOccupied,
        /// <summary>The destination bag cell does not exist (no equipped bag of that size).</summary>
        DestinationCellMissing,
    }

    /// <summary>
    /// INV-H5/H6, pure: the client's claimed coordinates must agree with the stored rows. The source cell is derived
    /// from the stored row (<c>src.Slot</c> / <c>src.StorageType</c>) and the claimed <c>ClientToPos(srcBag, srcSlot)</c>
    /// must equal it, so a forged source can neither move a row from a cell it is not in nor send the swap partner to a
    /// cell of the client's choosing. The destination must hold the claimed partner (<paramref name="dst"/> at the
    /// claimed cell) or, for <paramref name="dstUid"/> 0, be an empty cell. A bag-container destination must also
    /// exist (<see cref="BagCellExists"/>).
    /// </summary>
    /// <param name="rows">Everything the character owns, any StorageType.</param>
    internal static SwapRefusal CheckClaims(InventoryItem src, byte srcBag, byte srcSlot, InventoryItem? dst, ulong dstUid,
        byte dstBag, byte dstSlot, IReadOnlyCollection<InventoryItem> rows, Func<uint, ItemData?> lookup)
    {
        if (src.Id == dstUid) return SwapRefusal.SameItem;
        var (srcFlat, srcStorage) = ClientToPos(srcBag, srcSlot);
        if (src.Slot != srcFlat || src.StorageType != srcStorage) return SwapRefusal.SourceNotWhereClaimed;

        var (dstFlat, dstStorage) = ClientToPos(dstBag, dstSlot);
        if (dstUid != 0)
        {
            if (dst is null || dst.Slot != dstFlat || dst.StorageType != dstStorage) return SwapRefusal.DestinationMismatch;
        }
        else if (rows.Any(r => r.Id != src.Id && r.StorageType == dstStorage && r.Slot == dstFlat))
            return SwapRefusal.DestinationOccupied;

        if (dstStorage == StorageBag && !BagCellExists(dstFlat, rows, lookup)) return SwapRefusal.DestinationCellMissing;
        return SwapRefusal.None;
    }

    /// <summary>
    /// True when the flat StorageType-0 cell exists: the 20 main-bag cells always, an extra-bag cell only while a bag is
    /// equipped in that character-bag cell and the cell index is below its Bag_Size (BagCapacity geometry).
    /// </summary>
    internal static bool BagCellExists(int flat, IEnumerable<InventoryItem> rows, Func<uint, ItemData?> lookup)
    {
        if (flat < 0) return false;
        if (flat < BagCapacity.MainBagCells) return true;
        int bagCell = (flat - BagCapacity.MainBagCells) / BagCapacity.ExtraBagStride;
        int k = (flat - BagCapacity.MainBagCells) % BagCapacity.ExtraBagStride;
        if (bagCell >= BagCapacity.ExtraBagCount) return false;
        var bag = rows.FirstOrDefault(r => r.StorageType == StorageBagWindow && r.Slot == bagCell);
        return bag is not null && k < BagSizeOf(lookup(bag.ItemId));
    }

    private static int BagSizeOf(ItemData? bag) => bag is null ? 0 : Math.Clamp(bag.Bag_Size, 0, BagCapacity.ExtraBagStride);

    /// <summary>
    /// INV-H4, pure: would the swap leave a StorageType-0 row in a cell that stops existing? Looks only at the bag-window
    /// cells (StorageType 3) that held a bag before the swap (the source when it is worn there, the destination partner
    /// when the destination is a window cell). Each such cell is re-occupied by the swap partner (or by the source, or by
    /// nothing), and every row's position AFTER the swap is checked against the new bag's Bag_Size, so unequipping a bag
    /// that still holds items, swapping it for a smaller one, or dropping the bag into its own container is refused.
    /// Refuse, not re-home: re-homing would move rows the client is not told about. Rows already orphaned before the swap
    /// in a cell that is not touched are not counted.
    /// </summary>
    internal static bool BagWindowWouldStrandItems(InventoryItem src, int srcFlat, byte srcStorage, InventoryItem? dst,
        int dstFlat, byte dstStorage, IReadOnlyCollection<InventoryItem> rows, Func<uint, ItemData?> lookup)
    {
        var touched = new List<int>(2);
        if (srcStorage == StorageBagWindow) touched.Add(srcFlat);
        if (dstStorage == StorageBagWindow && dst is not null && !touched.Contains(dstFlat)) touched.Add(dstFlat);

        foreach (var cell in touched)
        {
            if (cell < 0 || cell >= BagCapacity.ExtraBagCount) continue;
            // The swap moves the source onto the destination cell and the partner onto the source cell.
            var occupant = srcStorage == StorageBagWindow && cell == srcFlat ? dst
                         : dstStorage == StorageBagWindow && cell == dstFlat ? src
                         : null;
            int first = BagCapacity.MainBagCells + BagCapacity.ExtraBagStride * cell + BagSizeOf(occupant is null ? null : lookup(occupant.ItemId));
            int end = BagCapacity.MainBagCells + BagCapacity.ExtraBagStride * (cell + 1);
            foreach (var r in rows)
            {
                var (slot, storage) = r.Id == src.Id ? ((int)dstFlat, dstStorage)
                                    : dst is not null && r.Id == dst.Id ? (srcFlat, srcStorage)
                                    : ((int)r.Slot, r.StorageType);
                if (storage == StorageBag && slot >= first && slot < end) return true;
            }
        }
        return false;
    }
}
