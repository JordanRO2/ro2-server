using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1552 ReqCardBookTakeOff_1Pcs - remove a single card from the book by card id.
/// Request (fixed 6 bytes): <c>[u32 cardId][u8 enforceGrade][u8 kind]</c>, kind = the book copy's bind bit (0/1).
/// The card is unregistered and re-created in the bag via CardService (no dup). Answers, in order,
/// 2333 OnPickupItem (the card back in the bag) -&gt; 2589 AnsUpdateCardStatus (the book record) -&gt;
/// 2801 AnsCardBookTakeOff_1Pcs <c>[u16 result]</c>.</summary>
public class CardBookTakeOff1PcsHandler : ZoneRmiHandlerBase<CardBookTakeOff1PcsHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardBookTakeOff_1Pcs;
    public override string Name => "CardBookTakeOff1Pcs";
    public CardBookTakeOff1PcsHandler(IServiceScopeFactory s, ILogger<CardBookTakeOff1PcsHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 // 1552 request layout.
    private const int CardIdSize = sizeof(uint);   // [u32 cardId] at offset 0
    private const int GradeOffset = 4;              // [u8 enforceGrade]
    private const int KindOffset = 5;               // [u8 kind]

 /// <summary>2801 result 0: the card was taken off.</summary>
    private const ushort ResultSuccess = 0;

 /// <summary>2801 result 22876 (0x595C): the take-off refusal for "that card is not in the book"; nothing is
 /// moved and neither 2333 nor 2589 is sent.</summary>
    private const ushort ResultNotInBook = 22876;

 /// <summary>2801 result 20020 (0x4E34): the no-free-bag-cell refusal, for a card that cannot join an existing
 /// stack (a card that can join one succeeds even when the bag has no free cell). 20020 is also the code the
 /// client's 2438 OnCommonError uses for a full bag.</summary>
    private const ushort ResultBagFull = 20020;

 /// <summary>2333 body+2 (pickup kind) on a take-off: 2 = the silent add (no pickup chat line; the item record is
 /// still applied, as the login re-send in AnsMapLoadingHandler relies on).</summary>
    private const ushort PickupKindTakeOff = 2;

 /// <summary>InventoryItem.StorageType 0: the item sits in a bag.</summary>
    private const byte StorageBag = 0;

 /// <summary>Marker for "no free bag cell" in the cell chosen for the returned card.</summary>
    private const short NoFreeCell = -1;

 /// <summary>2589 record byte +4 (flag): always 1 in a card-book update.</summary>
    private const byte CardStatusRecordFlag = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        uint cardId = 0;
        byte grade = 0;
        if (payload.Length >= CardIdSize)
            cardId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        // Request byte 4 is the enforce grade of the copy to take off. The book entry, the 2589 count and the
        // stack joined are all keyed on it, and the card returns to the bag at that grade.
        if (payload.Length > GradeOffset)
            grade = payload.Span[GradeOffset];
        // Request byte 5 is the book kind of the copy to take off (byte +7 of the 2587/2589 record): the copy's
        // bind bit, taken from the stack's bound flag when the card was put into the book (CardService.TakenOffRow).
        // The copy is matched on the kind the put stored, and the 2589 echoes it.
        byte kind = payload.Length > KindOffset && payload.Span[KindOffset] != 0 ? (byte)1 : (byte)0;

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // Where the returned card goes. It joins the bag's stack of that card when there is one, matched on card
        // id AND enforce grade (a different grade never joins) AND bind state (a bound copy joins only a bound
        // stack and an unbound one only an unbound stack, as a row holds one bind flag; CardStacks.FindJoinStack).
        // The stack cap is the item's Stack_Max (existing + 1 <= Stack_Max; Stack_Max 1 never stacks).
        // CardService re-creates the copy at the requested grade (Refine, the byte CardEnforceItemHandler
        // raises), so the stack it may join is one of that grade.
        // Otherwise it takes the first usable free cell: left to itself AddItemAsync takes the lowest free flat
        // slot with no upper bound, which can be a cell of a bag the player does not have.
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        var joinStack = CardStacks.FindJoinStack(owned, store.GetItem(cardId), cardId, grade, bound: kind != 0);
        var freeCells = BagCapacity.FreeCells(owned, store.Items);
        short cell = freeCells.Count > 0 ? freeCells[0] : NoFreeCell;
        var observed = new AddObservingInventory(inventory, cell, joinStack);
        var svc = new CardService(services.GetRequiredService<ICardBookRepository>(), store, observed, Logger);

        // 1) The card must be in the book at that grade. Counted per card id AND grade, as the 2589 count is.
        var book = await svc.GetBookAsync(player.CharacterId, ct);
        int copies = book.Cards.Count(e => e.CardId == cardId && e.EnforceGrade == grade && e.Kind == kind);
        if (cardId == 0 || copies == 0)
        {
            await SendResultAsync(session, ResultNotInBook, ct);
            Logger.LogInformation("{Name} take-off card {CardId}: not in book", player.Name, cardId);
            return;
        }

        // 2) The card must have somewhere to go: a stack to join, or a cell the client can show.
        if (joinStack is null && cell < 0)
        {
            await SendResultAsync(session, ResultBagFull, ct);
            Logger.LogInformation("{Name} take-off card {CardId}: bag full", player.Name, cardId);
            return;
        }

        // 3) Take it off through CardService (entry removed first, then the item re-created - no dup).
        var ok = await svc.TakeOffAsync(player.CharacterId, cardId, grade, kind, ct);
        Logger.LogInformation("{Name} removes card {CardId} (grade {Grade}) from book (ok={Ok}, slot={Slot}, stack={Count}, joined={Joined})",
            player.Name, cardId, grade, ok, observed.Added?.Slot, observed.Added?.Count, observed.Joined);
        if (!ok)
        {
            await SendResultAsync(session, ResultNotInBook, ct);   // lost a race with another take-off
            return;
        }

        // 2333 OnPickupItem - the card's bag row as it now stands (uid, cell, count), silent kind: the joined
        // stack with its new count, or the new row.
        if (observed.Added is { } added)
        {
            var pickup = LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(added),
                pickupKind: PickupKindTakeOff);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, w => w.WriteBytes(pickup), ct);
        }

        // 2589 AnsUpdateCardStatus - the book record after the take-off:
        // [u32 cardId][u8 flag = 1][u8 copies left][u8 enforceGrade][u8 kind]. The copy count falls by one per
        // take-off of the same card and grade and reaches 0 on the last one.
        var status = CardStatusPackets.BuildUpdateCardStatus2589(new[]
        {
            new CardStatusRecord(cardId, CardStatusRecordFlag, (byte)Math.Clamp(copies - 1, 0, byte.MaxValue), grade, kind),
        });
        await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateCardStatus, w => w.WriteBytes(status), ct);

        // 2801 AnsCardBookTakeOff_1Pcs - fixed 2 bytes: [u16 result]. 0 = success.
        await SendResultAsync(session, ResultSuccess, ct);
    }

    private static Task SendResultAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsCardBookTakeOff_1Pcs, writer => writer.WriteUInt16(result), ct);

 /// <summary>Passes everything through, but turns CardService's re-created card row into the bag change the
 /// handler chose: one more on the stack it found to join, or else the new row in the chosen free cell.
 /// Remembers the row that now holds the card, so the 2333 can carry it as stored (same pattern as
 /// MailGetItemHandler).</summary>
    private sealed class AddObservingInventory : IInventoryRepository
    {
        private readonly IInventoryRepository _inner;
        private readonly short _cell;
        private readonly InventoryItem? _join;
        public InventoryItem? Added { get; private set; }
        public bool Joined { get; private set; }

        public AddObservingInventory(IInventoryRepository inner, short cell, InventoryItem? join)
        {
            _inner = inner;
            _cell = cell;
            _join = join;
        }

        public async Task AddItemAsync(InventoryItem item)
        {
            // Only the unplaced bag row of the same card at the same grade joins; anything else is inserted.
            if (_join is not null && !Joined && item.Slot < 0 && item.StorageType == StorageBag
                && item.ItemId == _join.ItemId && item.Refine == _join.Refine)
            {
                _join.Count += Math.Max(item.Count, 1);
                await _inner.UpdateItemAsync(_join);
                Added = _join;
                Joined = true;
                return;
            }
            if (item.Slot < 0 && item.StorageType == StorageBag)
                item.Slot = _cell;
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
