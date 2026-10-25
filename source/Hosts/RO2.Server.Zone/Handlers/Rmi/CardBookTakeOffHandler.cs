using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1547 ReqCardBookTakeOff - drag copies of a card out of the card-book window onto a bag cell
/// (the single-copy take-off by button is 1552, <see cref="CardBookTakeOff1PcsHandler"/>). Answers like
/// 1552: 2333 OnPickupItem (the card in the bag) -&gt; 2589 AnsUpdateCardStatus (the book record) -&gt;
/// 2792 AnsCardBookTakeOff <c>[u16 result]</c>; a refusal gets the 2792 alone.</summary>
/// <remarks>
/// <para>
/// The request layout below is the client's own, and the answer follows the 1552/2801 sibling.
/// </para>
/// <para>
/// <b>Request</b>, NONE Blob9, built when a card-window list item (book mode) is dragged onto a bag cell, as
/// a 9-byte body:
/// <c>[u8 bag @0][u8 slot @1][u32 cardId @2, unaligned][u8 amount @6][u8 enforceGrade @7]
/// [u8 kind @8]</c>. Bag and slot are the cell's two bytes in the order the 1546 put builder sends them.
/// cardId is the key the card list uses (CardInfo.Card_ID == the card's ItemInfo ID). amount is the drag-split
/// quantity when the split flag is set, else 1. enforceGrade and kind are the same two bytes the 1552 builder
/// sends at body+4/+5 (the copy's enforce grade and its bind bit).
/// </para>
/// <para>
/// <b>Answer.</b> The client handles 2792 exactly like 2801: a non-zero u16 is shown as a result-code message, and 0
/// refreshes the card list over the client's LOCAL book data. So the 2792 itself
/// carries nothing more: the list shows the take-off only once a 2589 has updated the local book record, and the
/// bag shows the card only once a 2333 has placed it. Without them the book and the bag disagree until relog. The
/// codes are the 2801 ones: 22876 ("not enough item amount") when the card is not in the book
/// or has too few copies, 20020 when the cards have nowhere to go.
/// </para>
/// </remarks>
public class CardBookTakeOffHandler : ZoneRmiHandlerBase<CardBookTakeOffHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardBookTakeOff;
    public override string Name => "CardBookTakeOff";
    public CardBookTakeOffHandler(IServiceScopeFactory s, ILogger<CardBookTakeOffHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>The builder's Blob9 (see the class remarks).</summary>
    internal const int RequestLength = 9;

 // Request field offsets within the Blob9.
    private const int OffsetBag = 0;
    private const int OffsetSlot = 1;
    private const int OffsetCardId = 2;
    private const int OffsetAmount = 6;
    private const int OffsetEnforceGrade = 7;
    private const int OffsetKind = 8;

 // Client bag addressing: container 1 is the main bag (slots 50-69), containers 2-4 are the extra bags (slots 0-34).
    private const byte MainBagContainer = 1;
    private const byte MainBagFirstSlot = 50;
    private const byte MainBagEndSlot = MainBagFirstSlot + BagCapacity.MainBagCells;
    private const byte FirstExtraBagContainer = 2;
    private const byte LastExtraBagContainer = MainBagContainer + BagCapacity.ExtraBagCount;

 /// <summary>2589 record flag byte (record offset +4): always 1 in a card-book update.</summary>
    private const byte CardStatusRecordFlag = 1;

 /// <summary>The 2589 record's copy count is a single byte.</summary>
    private const int MaxRecordCopies = byte.MaxValue;

 /// <summary>2792 result 22876: the 2801 refusal for a card that is not in the book. The client shows "Fail to draw from
 /// Card illustrated book - not enough item amount". Used here also for fewer copies than the drag asks for.</summary>
    internal const ushort ResultNotInBook = 22876;

 /// <summary>2792 result 20020: the 2801 no-free-bag-cell refusal (CardBookTakeOff1PcsHandler.ResultBagFull).</summary>
    internal const ushort ResultBagFull = 20020;

 /// <summary>2333 body+2 on a take-off: 2 (the silent add).</summary>
    private const ushort PickupKindTakeOff = 2;

 /// <summary>The 1547 fields, as the client builder writes them.</summary>
    internal readonly record struct TakeOffRequest(byte Bag, byte Slot, uint CardId, byte Amount, byte EnforceGrade, byte Kind);

 /// <summary>Where the taken-off copies go: onto <see cref="Join"/> when it is set, else into the free flat
 /// <see cref="Cell"/>.</summary>
    internal readonly record struct Placement(InventoryItem? Join, short Cell);

 /// <summary>
 /// Reads the Blob9. amount 0 is read as 1 (the builder's own default) and a non-zero kind as 1 (the copy's bind
 /// bit, as 1552 reads it). False for a body shorter than 9 bytes.
 /// </summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out TakeOffRequest request)
    {
        request = default;
        if (body.Length < RequestLength) return false;
        request = new TakeOffRequest(
            Bag: body[OffsetBag],
            Slot: body[OffsetSlot],
            CardId: BinaryPrimitives.ReadUInt32LittleEndian(body[OffsetCardId..]),
            Amount: body[OffsetAmount] == 0 ? (byte)1 : body[OffsetAmount],
            EnforceGrade: body[OffsetEnforceGrade],
            Kind: body[OffsetKind] != 0 ? (byte)1 : (byte)0);
        return true;
    }

 /// <summary>The client's (bag, slot) as a flat inventory slot (container 1 slots 50-69 -&gt; 0-19, containers 2-4
 /// slots 0-34 -&gt; 20 + (bag-2)*35 + slot), or -1 for anything that is not a bag cell. The same mapping as
 /// GuildStorageItemGetHandler.BagSlotToFlat.</summary>
    internal static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == MainBagContainer) return slot is >= MainBagFirstSlot and < MainBagEndSlot ? slot - MainBagFirstSlot : -1;
        if (bag is >= FirstExtraBagContainer and <= LastExtraBagContainer && slot < BagCapacity.ExtraBagStride)
            return BagCapacity.MainBagCells + (bag - FirstExtraBagContainer) * BagCapacity.ExtraBagStride + slot;
        return -1;
    }

 /// <summary>
 /// Where <paramref name="amount"/> copies of <paramref name="cardId"/> at <paramref name="grade"/> with bind state
 /// <paramref name="bound"/> go, or null when nowhere (20020):
 /// the dropped-on cell when it is a free usable cell; else that cell's stack when it is this card at the same
 /// grade and bind state with room for all of them; else the 1552 placement (the first such stack, then the
 /// first free usable cell). All the copies go to one row, so a drag of more than the card's Stack_Max (1 for a
 /// card that does not stack) has nowhere to go; splitting such a drag is not defined.
 /// </summary>
    internal static Placement? ChoosePlacement(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items,
        ItemData? template, uint cardId, byte grade, bool bound, int amount, int requestedFlat)
    {
        int stackMax = template?.Stack_Max ?? 0;
        if (amount > Math.Max(stackMax, 1)) return null;

        byte bindType = template?.Bind_Type ?? 0;
        byte wanted = WorldEntryService.RecordBindFlags(bindType, 0, bound);
        bool CanJoin(InventoryItem i) =>
            stackMax > 1 && i.StorageType == 0 && i.Slot >= 0 && i.ItemId == cardId && i.Refine == grade
            && i.Count > 0 && i.Count + amount <= stackMax
            && WorldEntryService.RecordBindFlags(bindType, 0, i.Bound) == wanted;

        var free = BagCapacity.FreeCells(owned, items);
        if (requestedFlat >= 0)
        {
            if (free.Contains((short)requestedFlat)) return new Placement(null, (short)requestedFlat);
            var occupant = owned.FirstOrDefault(i => i.StorageType == 0 && i.Slot == requestedFlat);
            if (occupant is not null && CanJoin(occupant)) return new Placement(occupant, -1);
        }

        var join = owned.Where(CanJoin).OrderBy(i => i.Slot).FirstOrDefault();
        if (join is not null) return new Placement(join, -1);
        if (free.Count > 0) return new Placement(null, free[0]);
        return null;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest(payload.Span, out var req))
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqCardBookTakeOff (the builder sends {Want})", player.Name, payload.Length, RequestLength);
            await SendResultAsync(session, ResultNotInBook, ct);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);

        // 1) Enough copies of that card at that grade and kind in the book.
        var bookReader = new CardService(services.GetRequiredService<ICardBookRepository>(), store, inventory, Logger);
        var book = await bookReader.GetBookAsync(player.CharacterId, ct);
        int copies = book.Cards.Count(e => e.CardId == req.CardId && e.EnforceGrade == req.EnforceGrade && e.Kind == req.Kind);
        if (req.CardId == 0 || copies < req.Amount)
        {
            Logger.LogInformation("{Name} take-off of {Amount} x card {CardId} +{Grade} kind {Kind}: {Copies} in book",
                player.Name, req.Amount, req.CardId, req.EnforceGrade, req.Kind, copies);
            await SendResultAsync(session, ResultNotInBook, ct);
            return;
        }

        // 2) Somewhere for them to go.
        var placement = ChoosePlacement(owned, store.Items, store.GetItem(req.CardId), req.CardId, req.EnforceGrade,
            bound: req.Kind != 0, req.Amount, BagSlotToFlat(req.Bag, req.Slot));
        if (placement is not { } place)
        {
            Logger.LogInformation("{Name} take-off of {Amount} x card {CardId}: no bag cell (dropped on bag {Bag} slot {Slot})",
                player.Name, req.Amount, req.CardId, req.Bag, req.Slot);
            await SendResultAsync(session, ResultBagFull, ct);
            return;
        }

        // 3) One take-off per copy; the observing repository folds the re-created rows into the chosen placement.
        var observed = new AddObservingInventory(inventory, req.CardId, req.EnforceGrade, place);
        var svc = new CardService(services.GetRequiredService<ICardBookRepository>(), store, observed, Logger);
        int taken = 0;
        for (int i = 0; i < req.Amount; i++)
        {
            if (!await svc.TakeOffAsync(player.CharacterId, req.CardId, req.EnforceGrade, req.Kind, ct)) break;
            taken++;
        }
        Logger.LogInformation("{Name} takes {Taken}/{Amount} x card {CardId} +{Grade} kind {Kind} off the book (slot={Slot}, stack={Count}, joined={Joined})",
            player.Name, taken, req.Amount, req.CardId, req.EnforceGrade, req.Kind, observed.Added?.Slot, observed.Added?.Count, place.Join is not null);
        if (taken == 0)
        {
            await SendResultAsync(session, ResultNotInBook, ct);   // lost a race with another take-off
            return;
        }

        // 2333 OnPickupItem: the row that now holds the cards, silent kind.
        if (observed.Added is { } added)
        {
            var pickup = LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(added),
                pickupKind: PickupKindTakeOff);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, w => w.WriteBytes(pickup), ct);
        }

        // 2589 AnsUpdateCardStatus: the book record after the take-off, [cardId][flag][copies left][grade][kind].
        var status = CardStatusPackets.BuildUpdateCardStatus2589(new[]
        {
            new CardStatusRecord(req.CardId, CardStatusRecordFlag, (byte)Math.Clamp(copies - taken, 0, MaxRecordCopies), req.EnforceGrade, req.Kind),
        });
        await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateCardStatus, w => w.WriteBytes(status), ct);

        await SendResultAsync(session, 0, ct);
    }

 /// <summary>AnsCardBookTakeOff (2792), read_side U16: <c>[u16 result]</c>, 0 = success.</summary>
    private static Task SendResultAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsCardBookTakeOff, writer => writer.WriteUInt16(result), ct);

 /// <summary>Passes everything through, but turns CardService's re-created rows of the taken-off card into the
 /// chosen bag change: the first joins <see cref="Placement.Join"/> or is placed in <see cref="Placement.Cell"/>,
 /// and each later one adds one to that same row (the pattern of CardBookTakeOff1PcsHandler.AddObservingInventory).</summary>
    private sealed class AddObservingInventory : IInventoryRepository
    {
        private readonly IInventoryRepository _inner;
        private readonly uint _cardId;
        private readonly byte _grade;
        private readonly Placement _place;
        public InventoryItem? Added { get; private set; }

        public AddObservingInventory(IInventoryRepository inner, uint cardId, byte grade, Placement place)
        {
            _inner = inner;
            _cardId = cardId;
            _grade = grade;
            _place = place;
        }

        public async Task AddItemAsync(InventoryItem item)
        {
            if (item.Slot >= 0 || item.StorageType != 0 || item.ItemId != _cardId || item.Refine != _grade)
            {
                await _inner.AddItemAsync(item);
                return;
            }
            var target = Added ?? _place.Join;
            if (target is not null)
            {
                target.Count += Math.Max(item.Count, 1);
                await _inner.UpdateItemAsync(target);
                Added = target;
                return;
            }
            item.Slot = _place.Cell;
            await _inner.AddItemAsync(item);
            Added = item;
        }

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => _inner.GetByCharacterAsync(characterId);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => _inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => _inner.GetByIdAsync(id);
        public Task RemoveItemAsync(ulong itemId) => _inner.RemoveItemAsync(itemId);
        public Task UpdateItemAsync(InventoryItem item) => _inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => _inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => _inner.RepairSlotsAsync(characterId);
    }
}
