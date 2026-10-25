using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1346 ReqBankSwapItem (ByteArray) - swap item position in bank</summary>
public class BankSwapItemHandler : ZoneRmiHandlerBase<BankSwapItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBankSwapItem;
    public override string Name => "BankSwapItem";
    public BankSwapItemHandler(IServiceScopeFactory s, ILogger<BankSwapItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    // ---- 1346 request blob layout (offsets inside the blob after the compact prefix; +0..7 is unused) ----

    /// <summary>Blob offset of the source container id (u8). 5 = the bank window, 0..4 = an inventory container.</summary>
    private const int BlobSrcContainerOffset = 16;

    /// <summary>Blob offset of the source item uid (u64 LE).</summary>
    private const int BlobSrcUidOffset = 19;

    /// <summary>Blob offset of the destination container id (u8).</summary>
    private const int BlobDestContainerOffset = 28;

    /// <summary>Blob offset of the destination wire slot (u8); <see cref="AnyFreeSlot"/> asks for any free cell.</summary>
    private const int BlobDestSlotOffset = 29;

    /// <summary>Blob offset of the destination item uid (u64 LE), 0 when the destination cell is empty.</summary>
    private const int BlobDestUidOffset = 31;

    /// <summary>Size of a u64 uid field in the blob.</summary>
    private const int UidFieldLength = 8;

    /// <summary>Smallest blob that still contains the destination uid (offset 31 + 8 = 39 bytes).</summary>
    private const int MinBlobLength = BlobDestUidOffset + UidFieldLength;

    /// <summary>Destination wire-slot value meaning "any free cell" in the destination container.</summary>
    private const byte AnyFreeSlot = 0xFF;

    /// <summary>Destination uid value meaning the destination cell is empty.</summary>
    private const ulong EmptyCellUid = 0;

    // ---- Input-bound validation (InventoryRequest.Validate) ----

    /// <summary>Message tag and expected tag passed to Validate: the request has no tag field, so both are equal.</summary>
    private const ushort UnreadMsgTag = 0;

    /// <summary>Slot passed to Validate when the slot is not to be range-checked (the source slot, or an any-free destination).</summary>
    private const int SlotNotChecked = 0;

    /// <summary>Quantity and stack count passed to Validate: the request has no quantity field.</summary>
    private const int NoQuantity = 1;

    // ---- Item storage (InventoryItem.StorageType) ----

    /// <summary>StorageType of a plain bag cell (flat slot space, main bag plus extra bags).</summary>
    internal const byte StorageBag = 0;

    /// <summary>StorageType of a worn equipment slot.</summary>
    internal const byte StorageWorn = 1;

    /// <summary>StorageType of a bank cell.</summary>
    internal const byte StorageBank = 2;

    /// <summary>Slot value of a row parked outside the unique placement index (that index only covers Slot &gt;= 0).</summary>
    private const short UnplacedSlot = -1;

    // ---- Wire slot to storage mapping (MapPos) ----

    /// <summary>Highest container id of the main inventory window (containers 0 and 1).</summary>
    internal const byte MainWindowMaxContainer = 1;

    /// <summary>First container id of the extra bags (containers 2..4).</summary>
    private const byte FirstExtraBagContainer = 2;

    /// <summary>In the main inventory window, wire slots below this value are equipment slots.</summary>
    private const byte MainBagFirstWireSlot = 50;

    // ---- 126-byte item record offsets ----

    /// <summary>Record offset of the item template id (u32 LE).</summary>
    private const int RecordTemplateOffset = 8;

    /// <summary>Length of the template id field.</summary>
    private const int RecordTemplateLength = 4;

    /// <summary>Record offset of the stack count (u8).</summary>
    private const int RecordCountOffset = 14;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The ONE opcode retail uses for every bank deposit/withdraw/rearrange. Compact prefix then a
        // 40-byte blob (blob+0..7 is unused client data - do NOT read it):
        //   blob[16] srcContainer (5=bank, <=4=inventory), blob[19..26] srcUid(u64),
        //   blob[28] destContainer, blob[29] destSlot (0xFF = any free), blob[31..38] destUid(u64, 0 if empty).
        byte srcContainer, destContainer, destSlot;
        ulong srcUid, destUid;
        {
            // Lenient: the declared length is not compared, only that the blob reaches destUid.
            if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < MinBlobLength) return;
            srcContainer = blob[BlobSrcContainerOffset];
            srcUid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(BlobSrcUidOffset));
            destContainer = blob[BlobDestContainerOffset];
            destSlot = blob[BlobDestSlotOffset];
            destUid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(BlobDestUidOffset));
        }

        var inv = services.GetRequiredService<RO2.Application.Interfaces.IInventoryRepository>();
        var gameData = services.GetRequiredService<GameDataStore>();

        // INV-H12 input bounds. blob+0..7 is not read, so the tag and handle checks of Validate are fed
        // values that cannot fail; only the container, slot and quantity bounds act.
        // Defensive bounds, not a retail refusal.
        if (InventoryRequest.Validate(UnreadMsgTag, UnreadMsgTag, player.EntityId, player.EntityId, srcContainer,
                SlotNotChecked, NoQuantity, NoQuantity, maxContainer: BankPackets.BankBag) != InvReject.None
            || InventoryRequest.Validate(UnreadMsgTag, UnreadMsgTag, player.EntityId, player.EntityId, destContainer,
                   destSlot == AnyFreeSlot ? SlotNotChecked : destSlot, NoQuantity, NoQuantity,
                   maxContainer: BankPackets.BankBag) != InvReject.None)
        {
            Logger.LogInformation("{Name} sent a 1346 with an out-of-range container/slot (src {Src}, dest {Dest}/{Slot}); refused",
                player.Name, srcContainer, destContainer, destSlot);
            await SendBankRefresh(session, inv, player);
            return;
        }

        // INV-H3 bank-open: LOG-AND-ALLOW, not a refusal. The client sends no 1341; the bank window opens from the
        // NPC dialog with no server-visible open signal (1340 is a map-load fetch). Requiring the mark here would
        // refuse every legitimate NPC-bank 1346 and make the bank unusable in live play. Until the real open signal
        // is wired (1340 / the NPC-dialog bank action), this only logs; the bank-open requirement is defensive.
        if (!player.IsBankOpen(DateTime.UtcNow, BankOpenHandler.MaxRange))
            Logger.LogDebug("{Name} sent a 1346 with no open-bank mark in range (allowed; open signal not wired yet)", player.Name);
        var noDeposit = BankDepositTable.NoDepositIds;
        // Serialise the whole read-decide-write per character so two concurrent moves cannot interleave, and so the
        // swap below never leaves two rows on one cell (which the unique placement index rejects). Not re-entrant:
        // nothing inside takes this character's gate again.
        await CharacterInventoryGate.RunAsync(player.CharacterId, async () =>
        {
        var src = await inv.GetByIdAsync(srcUid);
        // Ownership guard (global uid space) — a client must not move another player's item.
        if (src is null || src.CharacterId != player.CharacterId)
        {
            await SendBankRefresh(session, inv, player);   // safe revert
            return 0;
        }

        byte[] recA, recB;

        if (destUid != EmptyCellUid)
        {
            // Destination cell occupied: swap the two items' (StorageType, Slot).
            var dst = await inv.GetByIdAsync(destUid);
            if (dst is null || dst.CharacterId != player.CharacterId) { await SendBankRefresh(session, inv, player); return 0; }
            // INV-H3: a direct bank<->worn swap, and a deposit of an Is_Deposit=0 template (either row landing in the bank),
            // are refused before anything is written (defensive rules).
            if (BankSwapRules.CrossesBankAndWorn(src.StorageType, dst.StorageType)
                || BankSwapRules.DepositBlocked(src.StorageType, dst.StorageType, src.ItemId, noDeposit)
                || BankSwapRules.DepositBlocked(dst.StorageType, src.StorageType, dst.ItemId, noDeposit))
            { await SendBankRefresh(session, inv, player); return 0; }
            // EQUIP-LEGALITY (SP0-EQUIP wiring): this swap can PLACE an item into a worn slot (MapPos yields
            // StorageType 1 for a container<=1, slot<50 target), so ownership alone is not enough. src will take
            // dst's cell and dst will take src's; validate whichever lands worn BEFORE the index-safe write order
            // below. Overlap (Equip_Slot_Overlap) enforcement is OFF — its semantics are unverified (see EquipRules).
            if (dst.StorageType == StorageWorn || src.StorageType == StorageWorn)
            {
                var actor = new EquipActor(player.Level, player.Job, player.Gender);
                var rows = await inv.GetByCharacterAsync(player.CharacterId);
                if (dst.StorageType == StorageWorn &&
                    EquipRules.CanPlace(src, gameData.GetItem(src.ItemId), dst.StorageType, dst.Slot, actor, rows, dst.Id) != EquipRefusal.None)
                { await SendBankRefresh(session, inv, player); return 0; }
                if (src.StorageType == StorageWorn &&
                    EquipRules.CanPlace(dst, gameData.GetItem(dst.ItemId), src.StorageType, src.Slot, actor, rows, src.Id) != EquipRefusal.None)
                { await SendBankRefresh(session, inv, player); return 0; }
            }
            var sStorage = src.StorageType; var sSlot = src.Slot;
            src.StorageType = dst.StorageType; src.Slot = dst.Slot;
            dst.StorageType = sStorage; dst.Slot = sSlot;
            // Index-safe write order (unique placement index is partial, only Slot>=0): park the destination row at
            // the exempt unplaced slot (-1), place the source into the freed destination cell, then settle the
            // destination into the vacated source cell. Three ordered writes, no transient two-on-one-cell. Final
            // slots and the 2344 answer are byte-identical to a plain two-write swap.
            var dstFinalSlot = dst.Slot;
            dst.Slot = UnplacedSlot;
            await inv.UpdateItemAsync(dst);     // (1) park destination at the exempt unplaced slot
            await inv.UpdateItemAsync(src);     // (2) source -> freed destination cell
            dst.Slot = dstFinalSlot;
            await inv.UpdateItemAsync(dst);     // (3) destination -> freed source cell
            recA = WorldEntryService.BuildItemRecord126(dst);   // now at the source cell
            recB = WorldEntryService.BuildItemRecord126(src);   // now at the dest cell
        }
        else
        {
            // Move to an empty cell. Resolve the destination (StorageType, FLAT slot) from the client's
            // (container, wire-slot). WITHDRAW FIX: the client sends an inventory dest slot in WIRE coords
            // (main bag 50-69), so writing it straight into Slot landed the item in an extra bag (flat>=20)
            // where it vanished — deposits worked only because the bank slot IS raw. Convert here.
            byte moveStorage; short newSlot = 0;
            if (destSlot == AnyFreeSlot)
                moveStorage = destContainer == BankPackets.BankBag ? StorageBank : StorageBag;
            else
                (moveStorage, newSlot) = MapPos(destContainer, destSlot);

            // INV-H3: bank<->worn direct, and a deposit of an Is_Deposit=0 template (defensive rules).
            if (BankSwapRules.CrossesBankAndWorn(src.StorageType, moveStorage)
                || BankSwapRules.DepositBlocked(src.StorageType, moveStorage, src.ItemId, noDeposit))
            {
                await SendBankRefresh(session, inv, player);
                return 0;
            }

            if (moveStorage == StorageBag)
            {
                // INV-H3: the destination bag cell must exist and be free (BagCapacity.FreeCells), whether the client named
                // a cell or asked for "any free" (0xFF). A withdraw into a full bag is answered with 2438 / 20020, the
                // refusal retail gives every other full-bag case (BagCapacity, CommonErrorPackets).
                var free = BagCapacity.FreeCells(await inv.GetByCharacterAsync(player.CharacterId), gameData.Items);
                var bag = BankSwapRules.ResolveBagCell(free, destSlot == AnyFreeSlot ? null : newSlot, destContainer, destSlot,
                    fromBank: src.StorageType == StorageBank);
                if (bag.Refusal != BankSwapRefusal.None)
                {
                    if (bag.Refusal == BankSwapRefusal.BagFull)
                        // Common error 2438 in its trade-refusal mode (0).
                        await session.SendRmiAsync(ZoneS2COpcodes.OnCommonError,
                            CommonErrorPackets.BuildCommonError2438(CommonErrorPackets.NoFreeBagCell, CommonErrorPackets.ModeTrade));
                    await SendBankRefresh(session, inv, player);
                    return 0;
                }
                newSlot = bag.Slot;
            }
            else if (destSlot == AnyFreeSlot)
            {
                newSlot = await FreeSlot(inv, player.CharacterId, moveStorage);
            }
            // Bank capacity guard on a move into the bank: the destination cell must exist in this character's bank
            // (cells 0 .. BankSlots-1, the count the client was given in the 2310 record+121 or the last 2345; see
            // BankExpansion) and the bank must have a free one. The client sizes its bank window with the same count,
            // so this only refuses what a client out of step with the server asks for.
            if (moveStorage == StorageBank)
            {
                int bankSlots = BankExpansion.SlotsOf(player.Character);
                var bankCount = System.Linq.Enumerable.Count(System.Linq.Enumerable.Where(
                    await inv.GetByCharacterAsync(player.CharacterId), i => i.StorageType == StorageBank && i.Id != src.Id));
                if (newSlot < 0 || newSlot >= bankSlots || bankCount >= bankSlots)
                {
                    await SendBankRefresh(session, inv, player);
                    return 0;
                }
            }
            // EQUIP-LEGALITY: a move whose destination is a worn slot (MapPos StorageType 1) must pass the equip
            // rules, as the swap branch does. vacatingUid 0 — the target cell was empty (destUid == 0). Overlap
            // enforcement OFF (see EquipRules).
            if (moveStorage == StorageWorn &&
                EquipRules.CanPlace(src, gameData.GetItem(src.ItemId), moveStorage, newSlot,
                    new EquipActor(player.Level, player.Job, player.Gender),
                    await inv.GetByCharacterAsync(player.CharacterId)) != EquipRefusal.None)
            {
                await SendBankRefresh(session, inv, player);
                return 0;
            }
            // OLD-cell CLEAR record: keep the bag+slot (the 2344 selector routes by it) but zero BOTH the
            // template (+0x08) and count (+0x0E) — the inventory window clears a cell on count==0 while the
            // bank window removes on template==0, so a fully-zeroed source clears in either direction.
            recA = WorldEntryService.BuildItemRecord126(src);
            recA.AsSpan(RecordTemplateOffset, RecordTemplateLength).Clear();   // template 0 -> bank removes
            recA[RecordCountOffset] = 0;                                        // count 0 -> inventory clears
            src.StorageType = moveStorage;
            src.Slot = newSlot;
            await inv.UpdateItemAsync(src);
            recB = WorldEntryService.BuildItemRecord126(src);   // moved item at its new cell
        }

        Logger.LogInformation("{Name} bank-moved uid {Uid} -> container {C} slot {S}",
            player.Name, srcUid, destContainer, destSlot);

        // Retail sends ONLY the 2344 per move — its two records each route to their own window by the bag
        // selector, so it removes the old cell AND places the new one in one shot. Re-sending the full 2338
        // after a move UPSERTS without clearing (the client's find-or-insert tree never erases), which is
        // exactly what duplicated items — so 2338 is the bank-OPEN packet only, never sent on a move.
        await session.SendRmiAsync(ZoneS2COpcodes.OnBankItemSwap,
            w => w.WriteBytes(BankPackets.BuildBankItemSwap2344(player.EntityId, recA, recB)));
        return 0;
        });
    }

    private static async Task SendBankRefresh(ProudSession session, RO2.Application.Interfaces.IInventoryRepository inv, PlayerState player)
    {
        var bank = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(
            System.Linq.Enumerable.Where(await inv.GetByCharacterAsync(player.CharacterId), i => i.StorageType == StorageBank),
            i => i.Slot));
        var records = new System.Collections.Generic.List<byte[]>(bank.Count);
        foreach (var it in bank) records.Add(WorldEntryService.BuildItemRecord126(it));
        await session.SendRmiAsync(ZoneS2COpcodes.OnBankData,
            w => w.WriteBytes(BankPackets.BuildBankData2338(player.EntityId, player.BankGold, records)));
    }

    /// <summary>Convert a client (container, wire-slot) to our (StorageType, FLAT slot). container 5 = the
    /// bank window (raw slot); container ≤1 with slot &lt;50 = equipment; ≤1 with slot 50-69 = main bag
    /// (flat = slot-50); containers 2-4 = extra bags.</summary>
    private static (byte storage, short slot) MapPos(byte container, byte slot)
    {
        if (container == BankPackets.BankBag) return (StorageBank, slot);
        if (container <= MainWindowMaxContainer && slot < MainBagFirstWireSlot) return (StorageWorn, slot);
        if (container <= MainWindowMaxContainer) return (StorageBag, (short)(slot - MainBagFirstWireSlot));
        return (StorageBag, (short)(BagCapacity.MainBagCells
            + (container - FirstExtraBagContainer) * BagCapacity.ExtraBagStride + slot));
    }

    private static async Task<short> FreeSlot(RO2.Application.Interfaces.IInventoryRepository inv, uint charId, byte storage)
    {
        var occupied = new System.Collections.Generic.HashSet<int>();
        foreach (var i in await inv.GetByCharacterAsync(charId))
            if (i.StorageType == storage && i.Slot >= 0) occupied.Add(i.Slot);
        short s = 0;
        while (occupied.Contains(s)) s++;
        return s;
    }
}

/// <summary>Why a 1346 move was refused by <see cref="BankSwapRules"/>; <see cref="None"/> = accepted.</summary>
internal enum BankSwapRefusal
{
    None,
    /// <summary>No free bag cell: answered 2438 / 20020.</summary>
    BagFull,
    /// <summary>The named bag cell does not exist, is not free, or is not a bag cell at all.</summary>
    BadDestination,
}

/// <summary>
/// Pure INV-H3 decisions of <see cref="BankSwapItemHandler"/> (the handler is not test-instantiable). Storage types are the
/// <c>InventoryItem.StorageType</c> values: 0 bag, 1 worn, 2 bank, 3 bag window.
/// These are defensive rules (bank&lt;-&gt;worn, Is_Deposit and bag-cell refusals), not refusals the retail
/// server is known to send.
/// </summary>
internal static class BankSwapRules
{
    /// <summary>Destination wire-slot value meaning "any free cell" (same value as the handler's request field).</summary>
    private const byte AnyFreeWireSlot = 0xFF;

    /// <summary>First wire slot (70..72) of the character-bag window in the main inventory containers.</summary>
    private const byte CharBagWindowFirstWireSlot = 70;

    /// <summary>True when exactly one side is worn (1) and the other is the bank (2): a direct bank&lt;-&gt;worn move.</summary>
    internal static bool CrossesBankAndWorn(byte a, byte b)
        => (a == BankSwapItemHandler.StorageWorn && b == BankSwapItemHandler.StorageBank)
        || (a == BankSwapItemHandler.StorageBank && b == BankSwapItemHandler.StorageWorn);

    /// <summary>True when an item of <paramref name="itemId"/> moving from <paramref name="from"/> to <paramref name="to"/>
    /// is a deposit (lands in the bank from outside it) of a template ItemInfo marks Is_Deposit 0.</summary>
    internal static bool DepositBlocked(byte from, byte to, uint itemId, IReadOnlySet<uint> noDeposit)
        => to == BankSwapItemHandler.StorageBank && from != BankSwapItemHandler.StorageBank && noDeposit.Contains(itemId);

    /// <summary>
    /// Picks the flat bag slot a move into the bag lands on, from <paramref name="free"/> (BagCapacity.FreeCells: every
    /// existing, unoccupied cell). <paramref name="requested"/> is the cell the client named (null for the 0xFF "any free
    /// cell"). Container &lt;= 1 with a wire slot of 70..72 is the character-bag window, not a bag cell. A withdraw
    /// (<paramref name="fromBank"/>) with no free cell is <see cref="BankSwapRefusal.BagFull"/>.
    /// </summary>
    internal static (BankSwapRefusal Refusal, short Slot) ResolveBagCell(IReadOnlyList<short> free, short? requested,
        byte destContainer, byte destSlot, bool fromBank)
    {
        if (destSlot != AnyFreeWireSlot && destContainer <= BankSwapItemHandler.MainWindowMaxContainer
            && destSlot >= CharBagWindowFirstWireSlot) return (BankSwapRefusal.BadDestination, 0);
        if (free.Count == 0) return (fromBank ? BankSwapRefusal.BagFull : BankSwapRefusal.BadDestination, 0);
        if (requested is not { } cell) return (BankSwapRefusal.None, free[0]);
        for (int i = 0; i < free.Count; i++)
            if (free[i] == cell) return (BankSwapRefusal.None, cell);
        return (BankSwapRefusal.BadDestination, 0);
    }
}

/// <summary>
/// The ItemInfo templates with <c>Is_Deposit</c> 0, read from data/ItemInfo.json: <see cref="ItemData"/>
/// has no such column and the shared model file is not edited (same approach as QuestInfoExtraColumns). An unreadable
/// table yields an empty set, so no deposit is blocked (fail open: the rule is defensive).
/// </summary>
internal static class BankDepositTable
{
    private sealed record Row(uint ID, byte Is_Deposit);

    private static readonly Lazy<IReadOnlySet<uint>> Ids = new(Load);

    internal static IReadOnlySet<uint> NoDepositIds => Ids.Value;

    private static IReadOnlySet<uint> Load()
    {
        var ids = new HashSet<uint>();
        try
        {
            var path = System.IO.Path.Combine("data", "ItemInfo.json");
            if (!System.IO.File.Exists(path)) return ids;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Row>>(System.IO.File.ReadAllBytes(path));
            foreach (var r in rows ?? new List<Row>())
                if (r.Is_Deposit == 0) ids.Add(r.ID);
        }
        catch
        {
            ids.Clear();
        }
        return ids;
    }
}
