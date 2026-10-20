using System.Buffers.Binary;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Cluster 1 (placement/swap family): INV-H1 (1325 all-or-nothing against a simulated end layout), INV-H4 (the 1324
/// legality gate for the character bag window and the stranded-contents refusal), INV-H5/H6 (1324 claimed coordinates,
/// degenerate swaps, merges) and INV-H10 (the durability wear selectors only take a row standing in a legal cell). The
/// decisions are pure helpers (<see cref="ItemSwapListHandler.Plan"/>, <see cref="ItemSwapHandler.CheckClaims"/>,
/// <see cref="ItemSwapHandler.BagWindowWouldStrandItems"/>, <see cref="DurabilityService.IsWornInLegalCell"/>) tested directly;
/// the persistence invariants run the handlers against <see cref="FakeInventoryRepository"/> with its unique placement
/// index on.
/// </summary>
public sealed class SwapFamilyHardeningTests
{
    private const uint CharId = 424242u;
    private const uint Me = 0x11u;

    // --- templates -------------------------------------------------------------------------------------------------
    private static readonly ItemData Sword = new() { ID = 1001, Item_Type = 1, Equip_Slot = 6, Equip_Type = 2 };
    private static readonly ItemData Armour = new() { ID = 1002, Item_Type = 2, Equip_Slot = 2, Equip_Type = 20 };
    private static readonly ItemData Potion = new() { ID = 2001, Item_Type = 11, Stack_Max = 20 };
    private static readonly ItemData Bag20 = new() { ID = 3001, Item_Type = 7, Bag_Size = 20 };
    private static readonly ItemData Bag10 = new() { ID = 3003, Item_Type = 7, Bag_Size = 10 };
    private static readonly ItemData Bag39 = new() { ID = 3002, Item_Type = 39, Bag_Size = 20 };   // Bag_Size without Item_Type 7
    private static readonly ItemData[] All = { Sword, Armour, Potion, Bag20, Bag10, Bag39 };

    private static readonly Dictionary<uint, ItemData> Templates = All.ToDictionary(t => t.ID);
    private static ItemData? Lookup(uint id) => Templates.GetValueOrDefault(id);

    private static GameDataStore Store()
    {
        var store = new GameDataStore();
        typeof(GameDataStore).GetProperty(nameof(GameDataStore.Items))!.SetValue(store, Templates.ToFrozenDictionary());
        return store;
    }

    private static InventoryItem Row(ulong id, uint itemId, byte storage, short slot, int count = 1) =>
        new() { Id = id, CharacterId = CharId, ItemId = itemId, Count = count, StorageType = storage, Slot = slot };

    // --- 1325 wire --------------------------------------------------------------------------------------------------
    private static byte[] Build1325(params (byte srcSlot, byte srcBag, ulong uid, byte dstSlot, byte dstBag)[] entries)
    {
        var p = new byte[10 + 13 * entries.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)(p.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 0xDEADBEEF);   // the client's own junk in the result field
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var o = 10 + 13 * i;
            p[o] = entries[i].srcSlot; p[o + 1] = entries[i].srcBag;
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(o + 2), entries[i].uid);
            p[o + 10] = 1;
            p[o + 11] = entries[i].dstSlot; p[o + 12] = entries[i].dstBag;
        }
        return p;
    }

    private static SwapListEntry E(byte srcSlot, byte srcBag, ulong uid, byte dstSlot, byte dstBag) => new(srcSlot, srcBag, uid, dstSlot, dstBag);

    // --- 1324 wire --------------------------------------------------------------------------------------------------
    private static byte[] Build1324(byte srcSlot, byte srcBag, ulong srcUid, byte srcQty, byte dstSlot, byte dstBag, ulong dstUid,
        ushort tag = ItemSwapHandler.MsgTag, uint handle = Me)
    {
        var blob = new byte[30];
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), handle);
        blob[8] = srcSlot; blob[9] = srcBag;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(10), srcUid);
        blob[18] = srcQty;
        blob[19] = dstSlot; blob[20] = dstBag;
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(21), dstUid);
        var payload = new byte[2 + blob.Length];
        payload[0] = 1; payload[1] = (byte)blob.Length;
        blob.CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>Wire cell of a flat storage-0 slot: main bag flat 0..19 = bag 1 slot 50..69; extra bags are bag 2..4.</summary>
    private static (byte slot, byte bag) Bag(int flat) =>
        flat < 20 ? ((byte)(50 + flat), (byte)1) : ((byte)((flat - 20) % 35), (byte)(2 + (flat - 20) / 35));

    // --- rig: a harness with one player, the fake repository (unique index ON) and both handlers --------------------
    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required FakeInventoryRepository Inv { get; init; }
        public required ItemSwapHandler Swap { get; init; }
        public required ItemSwapListHandler List { get; init; }

        public static async Task<Rig> StartAsync(params InventoryItem[] rows)
        {
            var net = await Harness.StartAsync(1);
            var inv = new FakeInventoryRepository(new RepoScheduler());
            foreach (var r in rows) inv.Seed(r);
            Assert.True(await inv.EnsureIndexesAsync());
            var players = new PlayerStateManager(new NoDirtySink());
            var player = new PlayerState
            {
                SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Hero", EntityId = Me,
                Level = 60, Job = 7, Gender = 1,
            };
            players.AddPlayer(player.SessionId, player);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            var services = new ServiceCollection()
                .AddScoped<IInventoryRepository>(_ => inv)
                .AddSingleton(Store())
                .AddSingleton(TestEmblems.Create())
                .BuildServiceProvider();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            return new Rig
            {
                Net = net, Inv = inv,
                Swap = new ItemSwapHandler(scopes, NullLogger<ItemSwapHandler>.Instance, players, broadcast),
                List = new ItemSwapListHandler(scopes, NullLogger<ItemSwapListHandler>.Instance, players, broadcast),
            };
        }

        public InventoryItem Get(ulong id) => Inv.Rows.Single(r => r.Id == id);

        public ValueTask DisposeAsync() => Net.DisposeAsync();
    }

    // ================================ INV-H1: 1325 (pure plan) ======================================================

    [Fact]
    public void Plan_NonWeaponIntoBag1Slot6_RejectsTheWholeBatch()
    {
        var rows = new[] { Row(1, Armour.ID, 0, 0), Row(2, Potion.ID, 0, 1) };
        // The first entry is a legal swap-away; the second targets the equipment window (bag 1 slot 6). Nothing may move.
        var refusal = ItemSwapListHandler.Plan(
            [E(50, 1, 1, 52, 1), E(51, 1, 2, 6, 1)], 2, CharId, rows, Lookup, out var moves);
        Assert.Equal(SwapListRefusal.NotBagCell, refusal);
        Assert.Empty(moves);
    }

    [Theory]
    [InlineData(6, 1)]    // worn window
    [InlineData(70, 1)]   // character-bag window
    public void Plan_AnyNonBagTarget_IsRefused(int slot, int bag)
    {
        var rows = new[] { Row(1, Bag20.ID, 0, 0) };
        Assert.Equal(SwapListRefusal.NotBagCell,
            ItemSwapListHandler.Plan([E(50, 1, 1, (byte)slot, (byte)bag)], 1, CharId, rows, Lookup, out _));
    }

    [Fact]
    public void Plan_AWornRowCannotBeMovedByTheSort()
    {
        var rows = new[] { Row(1, Sword.ID, 1, 6) };
        Assert.Equal(SwapListRefusal.NotBagCell,
            ItemSwapListHandler.Plan([E(6, 1, 1, 51, 1)], 1, CharId, rows, Lookup, out _));
    }

    [Fact]
    public void Plan_ThousandEntries_NeverShareACell()
    {
        // 1000 owned bag rows, none placed; every entry wants one of the 20 main-bag cells. At most 20 can land.
        var rows = Enumerable.Range(1, 1000).Select(i => Row((ulong)i, Potion.ID, 0, -1)).ToArray();
        var entries = Enumerable.Range(1, 1000).Select(i => E(50, 1, (ulong)i, (byte)(50 + i % 20), 1)).ToArray();
        var refusal = ItemSwapListHandler.Plan(entries, entries.Length, CharId, rows, Lookup, out var moves);
        Assert.Equal(SwapListRefusal.CellCollision, refusal);
        Assert.Empty(moves);
    }

    [Fact]
    public void Plan_ALegalPermutation_IsAccepted_AndEveryCellIsUsedOnce()
    {
        // A full main bag reversed: flat i -> 19 - i. Every cell is both vacated and re-filled in the same batch.
        var rows = Enumerable.Range(0, 20).Select(i => Row((ulong)(i + 1), Potion.ID, 0, (short)i)).ToArray();
        var entries = Enumerable.Range(0, 20).Select(i => E((byte)(50 + i), 1, (ulong)(i + 1), (byte)(50 + 19 - i), 1)).ToArray();
        var refusal = ItemSwapListHandler.Plan(entries, 20, CharId, rows, Lookup, out var moves);
        Assert.Equal(SwapListRefusal.None, refusal);
        Assert.Equal(20, moves.Count);
        Assert.Equal(20, moves.Select(m => m.Flat).Distinct().Count());
    }

    [Fact]
    public void Plan_LandingOnARowTheBatchDoesNotMove_IsACollision()
    {
        var rows = new[] { Row(1, Potion.ID, 0, 0), Row(2, Potion.ID, 0, 1) };
        Assert.Equal(SwapListRefusal.CellCollision,
            ItemSwapListHandler.Plan([E(50, 1, 1, 51, 1)], 1, CharId, rows, Lookup, out _));
    }

    [Fact]
    public void Plan_UnknownForeignDuplicateMalformedAndBadCoordinates_AreRefused()
    {
        var rows = new[]
        {
            Row(1, Potion.ID, 0, 0), Row(2, Potion.ID, 0, 1),
            new InventoryItem { Id = 9, CharacterId = CharId + 1, ItemId = Potion.ID, Count = 1, StorageType = 0, Slot = 5 },
        };
        Assert.Equal(SwapListRefusal.UnknownItem, ItemSwapListHandler.Plan([E(50, 1, 77, 55, 1)], 1, CharId, rows, Lookup, out _));
        Assert.Equal(SwapListRefusal.UnknownItem, ItemSwapListHandler.Plan([E(50, 1, 9, 55, 1)], 1, CharId, rows, Lookup, out _));   // another character's row
        Assert.Equal(SwapListRefusal.DuplicateItem, ItemSwapListHandler.Plan([E(50, 1, 1, 55, 1), E(50, 1, 1, 56, 1)], 2, CharId, rows, Lookup, out _));
        Assert.Equal(SwapListRefusal.Malformed, ItemSwapListHandler.Plan([E(50, 1, 1, 55, 1)], 2, CharId, rows, Lookup, out _));
        Assert.Equal(SwapListRefusal.BadCoordinate, ItemSwapListHandler.Plan([E(50, 1, 1, 55, 0)], 1, CharId, rows, Lookup, out _));    // bag 0
        Assert.Equal(SwapListRefusal.BadCoordinate, ItemSwapListHandler.Plan([E(50, 1, 1, 55, 5)], 1, CharId, rows, Lookup, out _));    // bank container
        Assert.Equal(SwapListRefusal.BadCoordinate, ItemSwapListHandler.Plan([E(50, 9, 1, 55, 1)], 1, CharId, rows, Lookup, out _));    // bad source bag
        Assert.Equal(SwapListRefusal.BadCoordinate, ItemSwapListHandler.Plan([E(50, 1, 1, 200, 1)], 1, CharId, rows, Lookup, out _));   // slot out of range
    }

    [Fact]
    public void Plan_AnExtraBagCellNeedsAnEquippedBagOfThatSize()
    {
        var item = Row(1, Potion.ID, 0, 0);
        var (s, b) = Bag(20 + 5);   // container 2, cell 5
        Assert.Equal(SwapListRefusal.CellDoesNotExist, ItemSwapListHandler.Plan([E(50, 1, 1, s, b)], 1, CharId, [item], Lookup, out _));
        // A 10-cell bag in window cell 0: cell 5 exists, cell 12 does not.
        var withBag = new[] { item, Row(2, Bag10.ID, 3, 0) };
        Assert.Equal(SwapListRefusal.None, ItemSwapListHandler.Plan([E(50, 1, 1, s, b)], 1, CharId, withBag, Lookup, out _));
        var (s12, b12) = Bag(20 + 12);
        Assert.Equal(SwapListRefusal.CellDoesNotExist, ItemSwapListHandler.Plan([E(50, 1, 1, s12, b12)], 1, CharId, withBag, Lookup, out _));
    }

    // ================================ INV-H1: 1325 (persistence) ===================================================

    [Fact]
    public async Task Persist_APermutation_IsIndexSafe_AndLandsEveryRowOnItsCell()
    {
        await using var rig = await Rig.StartAsync(
            Enumerable.Range(0, 4).Select(i => Row((ulong)(i + 1), Potion.ID, 0, (short)i)).ToArray());
        var rows = await rig.Inv.GetByCharacterAsync(CharId);
        // 1<->2 swap and a 3 -> 4 -> 3 cycle: every target is another mover's current cell.
        var moves = new List<(InventoryItem, short)> { (rows[0], 1), (rows[1], 0), (rows[2], 3), (rows[3], 2) };
        Assert.True(await ItemSwapListHandler.PersistAsync(rig.Inv, moves, NullLogger.Instance));
        Assert.Equal(new short[] { 1, 0, 3, 2 }, new[] { 1UL, 2UL, 3UL, 4UL }.Select(id => rig.Get(id).Slot).ToArray());
    }

    /// <summary>Delegates to a fake but throws on the n-th UpdateItemAsync, to prove the batch rolls back.</summary>
    private sealed class FailingUpdates(IInventoryRepository inner, int failOn) : IInventoryRepository
    {
        private int _updates;
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint c) => inner.GetByCharacterAsync(c);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint c) => inner.GetEquipmentAsync(c);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => inner.GetByIdAsync(id);
        public Task AddItemAsync(InventoryItem item) => inner.AddItemAsync(item);
        public Task RemoveItemAsync(ulong id) => inner.RemoveItemAsync(id);
        public Task UpdateItemAsync(InventoryItem item) =>
            Interlocked.Increment(ref _updates) == failOn ? throw new InvalidOperationException("injected") : inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint c) => inner.RepairSlotsAsync(c);
    }

    [Fact]
    public async Task Persist_AWriteFailure_RollsTheWholeBatchBack()
    {
        await using var rig = await Rig.StartAsync(
            Enumerable.Range(0, 4).Select(i => Row((ulong)(i + 1), Potion.ID, 0, (short)i)).ToArray());
        var rows = await rig.Inv.GetByCharacterAsync(CharId);
        var moves = new List<(InventoryItem, short)> { (rows[0], 1), (rows[1], 0), (rows[2], 3), (rows[3], 2) };
        // Update #6 is the second placement write: 4 parks + 1 placement succeeded, then it throws.
        Assert.False(await ItemSwapListHandler.PersistAsync(new FailingUpdates(rig.Inv, 6), moves, NullLogger.Instance));
        Assert.Equal(new short[] { 0, 1, 2, 3 }, new[] { 1UL, 2UL, 3UL, 4UL }.Select(id => rig.Get(id).Slot).ToArray());
    }

    [Fact]
    public async Task Handler1325_NonWeaponIntoBag1Slot6_IsRefusedAndNothingMoves()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0), Row(2, Armour.ID, 0, 1));
        // Entry 1 is a legal move; entry 2 puts the armour in the weapon hand. All-or-nothing: neither lands.
        await rig.List.HandleAsync(rig.Net.Sessions[0], Build1325((50, 1, 1, 60, 1), (51, 1, 2, 6, 1)));
        var (_, body) = await rig.Net.Readers[0].NextAsync();
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)) > 0, "the client must not apply a refused batch");
        Assert.Equal(((byte)0, (short)0), (rig.Get(1).StorageType, rig.Get(1).Slot));
        Assert.Equal(((byte)0, (short)1), (rig.Get(2).StorageType, rig.Get(2).Slot));
    }

    [Fact]
    public async Task Handler1325_ALegalSwap_PersistsAndAnswersResultZero()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0), Row(2, Armour.ID, 0, 1));
        await rig.List.HandleAsync(rig.Net.Sessions[0], Build1325((50, 1, 1, 51, 1), (51, 1, 2, 50, 1)));
        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(RO2.Network.Protocol.ZoneS2COpcodes.OnPCItemSwapList, op);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal((short)1, rig.Get(1).Slot);
        Assert.Equal((short)0, rig.Get(2).Slot);
    }

    // ================================ INV-H5/H6: 1324 claims (pure) =================================================

    [Fact]
    public void CheckClaims_TheSourceMustBeWhereTheStoredRowIs()
    {
        var src = Row(1, Potion.ID, 0, 3);
        Assert.Equal(ItemSwapHandler.SwapRefusal.None, ItemSwapHandler.CheckClaims(src, 1, 53, null, 0, 1, 55, [src], Lookup));
        Assert.Equal(ItemSwapHandler.SwapRefusal.SourceNotWhereClaimed, ItemSwapHandler.CheckClaims(src, 1, 50, null, 0, 1, 55, [src], Lookup));   // claims flat 0
        Assert.Equal(ItemSwapHandler.SwapRefusal.SourceNotWhereClaimed, ItemSwapHandler.CheckClaims(src, 1, 3, null, 0, 1, 55, [src], Lookup));    // claims worn slot 3
        var banked = Row(2, Potion.ID, 2, 3);   // a bank row can never be claimed through bag/slot
        Assert.Equal(ItemSwapHandler.SwapRefusal.SourceNotWhereClaimed, ItemSwapHandler.CheckClaims(banked, 1, 53, null, 0, 1, 55, [banked], Lookup));
    }

    [Fact]
    public void CheckClaims_TheDestinationMustHoldThePartnerOrBeAnEmptyExistingCell()
    {
        var src = Row(1, Potion.ID, 0, 0);
        var other = Row(2, Armour.ID, 0, 5);
        var rows = new[] { src, other };
        Assert.Equal(ItemSwapHandler.SwapRefusal.None, ItemSwapHandler.CheckClaims(src, 1, 50, other, 2, 1, 55, rows, Lookup));
        Assert.Equal(ItemSwapHandler.SwapRefusal.DestinationMismatch, ItemSwapHandler.CheckClaims(src, 1, 50, other, 2, 1, 56, rows, Lookup));   // partner is not there
        Assert.Equal(ItemSwapHandler.SwapRefusal.DestinationMismatch, ItemSwapHandler.CheckClaims(src, 1, 50, null, 99, 1, 56, rows, Lookup));   // uid does not exist
        Assert.Equal(ItemSwapHandler.SwapRefusal.DestinationOccupied, ItemSwapHandler.CheckClaims(src, 1, 50, null, 0, 1, 55, rows, Lookup));    // "empty" cell holds a row
        Assert.Equal(ItemSwapHandler.SwapRefusal.None, ItemSwapHandler.CheckClaims(src, 1, 50, null, 0, 1, 56, rows, Lookup));
        var (s, b) = Bag(30);   // container 2, no bag equipped
        Assert.Equal(ItemSwapHandler.SwapRefusal.DestinationCellMissing, ItemSwapHandler.CheckClaims(src, 1, 50, null, 0, b, s, rows, Lookup));
        Assert.Equal(ItemSwapHandler.SwapRefusal.SameItem, ItemSwapHandler.CheckClaims(src, 1, 50, src, 1, 1, 50, rows, Lookup));
    }

    // ================================ INV-H4: bag window (pure) =====================================================

    [Fact]
    public void BagWindowStrand_UnequippingABagThatHoldsItems_IsRefused()
    {
        var bag = Row(1, Bag20.ID, 3, 0);
        var held = Row(2, Potion.ID, 0, 23);        // container 2, cell 3
        var mainBagCell = 1;
        Assert.True(ItemSwapHandler.BagWindowWouldStrandItems(bag, 0, 3, null, mainBagCell, 0, [bag, held], Lookup));
        Assert.False(ItemSwapHandler.BagWindowWouldStrandItems(bag, 0, 3, null, mainBagCell, 0, [bag], Lookup));   // empty bag: free to leave
    }

    [Fact]
    public void BagWindowStrand_ASmallerReplacementBagStrandsTheCellsPastItsSize()
    {
        var worn = Row(1, Bag20.ID, 3, 0);
        var held = Row(2, Potion.ID, 0, 20 + 15);   // cell 15 of the 20-cell bag
        var small = Row(3, Bag10.ID, 0, 4);
        var big = Row(4, Bag20.ID, 0, 4);
        // Source = the bag in the main bag, destination partner = the worn bag it displaces.
        Assert.True(ItemSwapHandler.BagWindowWouldStrandItems(small, 4, 0, worn, 0, 3, [worn, held, small], Lookup));
        Assert.False(ItemSwapHandler.BagWindowWouldStrandItems(big, 4, 0, worn, 0, 3, [worn, held, big], Lookup));
    }

    [Fact]
    public void BagWindowStrand_AnEmptyWindowCellIsNotChecked_SoLegacyOrphansDoNotBlockEquipping()
    {
        var orphan = Row(2, Potion.ID, 0, 20 + 15);   // a row left in a container with no bag
        var bag = Row(1, Bag10.ID, 0, 4);
        Assert.False(ItemSwapHandler.BagWindowWouldStrandItems(bag, 4, 0, null, 0, 3, [bag, orphan], Lookup));
    }

    [Fact]
    public void BagWindowStrand_DroppingTheBagIntoItsOwnContainer_IsRefused()
    {
        var bag = Row(1, Bag20.ID, 3, 0);
        Assert.True(ItemSwapHandler.BagWindowWouldStrandItems(bag, 0, 3, null, 25, 0, [bag], Lookup));
    }

    // ================================ 1324 handler oracles ==========================================================

    [Fact]
    public async Task Handler1324_Item_Type39_IntoTheBagWindow_IsRefused_AndItem_Type7Equips()
    {
        await using var rig = await Rig.StartAsync(Row(1, Bag39.ID, 0, 0), Row(2, Bag20.ID, 0, 1));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 1, 70, 1, 0));   // Item_Type 39 -> window cell 0
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(((byte)0, (short)0), (rig.Get(1).StorageType, rig.Get(1).Slot));

        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(51, 1, 2, 1, 70, 1, 0));   // Item_Type 7 -> window cell 0
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(((byte)3, (short)0), (rig.Get(2).StorageType, rig.Get(2).Slot));
    }

    [Fact]
    public async Task Handler1324_UnequippingABagWithItemsInIt_IsRefused_AndAnEmptyOneIsNot()
    {
        await using var rig = await Rig.StartAsync(Row(1, Bag20.ID, 3, 0), Row(2, Potion.ID, 0, 23));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(70, 1, 1, 1, 52, 1, 0));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(((byte)3, (short)0), (rig.Get(1).StorageType, rig.Get(1).Slot));   // still worn

        await rig.Inv.RemoveItemAsync(2UL);
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(70, 1, 1, 1, 52, 1, 0));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(((byte)0, (short)2), (rig.Get(1).StorageType, rig.Get(1).Slot));
    }

    [Fact]
    public async Task Handler1324_ForgedSourceCoordinates_SendRevert_AndNothingMoves()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 3), Row(2, Armour.ID, 0, 7));
        // The stored row is at flat 3 (bag 1 slot 53). The client claims it is at flat 0 and drops it on the empty flat 9.
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 1, 59, 1, 0));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal((short)3, rig.Get(1).Slot);
        Assert.Equal((short)7, rig.Get(2).Slot);

        // The honest claim moves it.
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(53, 1, 1, 1, 59, 1, 0));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal((short)9, rig.Get(1).Slot);
    }

    [Fact]
    public async Task Handler1324_AForgedDestinationPartner_SendsRevert()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0), Row(2, Armour.ID, 0, 5), Row(3, Armour.ID, 0, 6));
        // dst uid 2 is at flat 5 but the client says flat 6 (where row 3 sits): the partner would be dragged to flat 0.
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 1, 56, 1, 2));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(new short[] { 0, 5, 6 }, new[] { 1UL, 2UL, 3UL }.Select(id => rig.Get(id).Slot).ToArray());
    }

    [Fact]
    public async Task Handler1324_AnEmptyDestinationThatIsOccupied_SendsRevert()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0), Row(2, Armour.ID, 0, 5));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 1, 55, 1, 0));   // "empty" cell 5 holds row 2
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal((short)0, rig.Get(1).Slot);
        Assert.Equal((short)5, rig.Get(2).Slot);
    }

    [Fact]
    public async Task Handler1324_SwapWithItself_LeavesTheCountUnchanged()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0, count: 5));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 1, 50, 1, 1));   // ItemSwap(A, A, 1)
        await rig.Net.Readers[0].NextAsync();
        var rows = rig.Inv.Rows;
        Assert.Single(rows);
        Assert.Equal(5, rows[0].Count);
    }

    [Fact]
    public async Task Handler1324_AMergeStillCombinesStacksOfTheSameContainer()
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0, count: 3), Row(2, Potion.ID, 0, 1, count: 4));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 3, 51, 1, 2));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(7, rig.Get(2).Count);
        Assert.DoesNotContain(rig.Inv.Rows, r => r.Id == 1);
    }

    [Fact]
    public async Task Handler1324_AStackNeverMergesAcrossContainerTypes()
    {
        // A (pathological) stackable row in the bag window with the same template as the bag stack dragged onto it.
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0, count: 3), Row(2, Potion.ID, 3, 0, count: 4));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, 3, 70, 1, 2));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal(3, rig.Get(1).Count);
        Assert.Equal(4, rig.Get(2).Count);
        Assert.Equal(((byte)0, (short)0), (rig.Get(1).StorageType, rig.Get(1).Slot));   // and the swap into the window was refused too
    }

    [Theory]
    [InlineData((ushort)0, Me, (byte)1)]          // no tag
    [InlineData(ItemSwapHandler.MsgTag, 0x99u, (byte)1)]   // someone else's handle
    [InlineData(ItemSwapHandler.MsgTag, Me, (byte)0)]      // quantity 0
    [InlineData(ItemSwapHandler.MsgTag, Me, (byte)9)]      // more than the stack holds
    public async Task Handler1324_BadTagHandleOrQuantity_SendsRevert(ushort tag, uint handle, byte qty)
    {
        await using var rig = await Rig.StartAsync(Row(1, Potion.ID, 0, 0, count: 5));
        await rig.Swap.HandleAsync(rig.Net.Sessions[0], Build1324(50, 1, 1, qty, 51, 1, 0, tag, handle));
        await rig.Net.Readers[0].NextAsync();
        Assert.Equal((short)0, rig.Get(1).Slot);
    }

    // ================================ INV-H10: wear selectors =======================================================

    [Fact]
    public void WornInLegalCell_FollowsEquipRulesWornSlotsFor()
    {
        var ring = new ItemData { ID = 5, Equip_Slot = 10, Item_Type = 3 };
        var earring = new ItemData { ID = 6, Equip_Slot = 9, Item_Type = 3 };
        InventoryItem Worn(short slot, byte storage = 1) => new() { Id = 1, StorageType = storage, Slot = slot };
        Assert.True(DurabilityService.IsWornInLegalCell(Worn(6), Sword));
        Assert.False(DurabilityService.IsWornInLegalCell(Worn(7), Sword));
        Assert.False(DurabilityService.IsWornInLegalCell(Worn(6, storage: 0), Sword));
        Assert.True(DurabilityService.IsWornInLegalCell(Worn(10), ring));
        Assert.True(DurabilityService.IsWornInLegalCell(Worn(11), ring));
        Assert.False(DurabilityService.IsWornInLegalCell(Worn(9), ring));
        Assert.True(DurabilityService.IsWornInLegalCell(Worn(9), earring));
        Assert.False(DurabilityService.IsWornInLegalCell(Worn(10), earring));
    }

    [Fact]
    public void WearSelectors_SkipARowInAnIllegalCell()
    {
        var legalSword = new InventoryItem { Id = 1, ItemId = Sword.ID, StorageType = 1, Slot = 6 };
        var ghostSword = new InventoryItem { Id = 2, ItemId = Sword.ID, StorageType = 1, Slot = 3 };      // a weapon parked in an armour cell
        var legalArmour = new InventoryItem { Id = 3, ItemId = Armour.ID, StorageType = 1, Slot = 2 };
        var ghostArmour = new InventoryItem { Id = 4, ItemId = Armour.ID, StorageType = 1, Slot = 20 };   // armour in a card cell
        var all = new[] { ghostSword, legalSword, ghostArmour, legalArmour };

        Assert.Equal(new[] { legalSword }, DurabilityService.SelectWornWeapons(all, Lookup));
        Assert.Equal(new[] { legalArmour }, DurabilityService.SelectWornArmour(all, Lookup).Select(a => a.Item).ToArray());
    }
}
