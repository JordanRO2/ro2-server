using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Network.Protocol;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Economy.Domain;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Tests.Economy;

/// <summary>
/// Cluster T (trade) of the economy hardening, driven through the REAL 1353/1354/1355/1357 handlers and the real
/// disconnect path over loopback sessions, on <see cref="EconomyFixture"/>'s fakes: E-H3 (the swap's item moves are
/// atomic, zeny moves after every item, a failure refunds), E-H4 (an offer is bound to the instance that was shown),
/// E-H9 (1354 leaks nothing), E-H10 (1355 is clamped to the wallet) and E-H8 (a disconnect ends the trade).
/// </summary>
public sealed class TradeEconomyHardeningTests
{
    private const uint CharA = 7_201_001u;
    private const uint CharB = 7_201_002u;
    private const uint HandleA = 0x11_01_11u;
    private const uint HandleB = 0x12_01_11u;

    private const uint Single = 5101u;      // not stackable
    private const uint SingleOther = 5103u; // a different template, the "swap under the same uid"
    private const uint Stackable = 5102u;   // Stack_Max 120

    // Two ProudServer sessions get ids 1 and 2 in EVERY harness, and EconomyConcurrencyHardeningTests drives the
    // process-wide TradeSessionService.Instance with those same ids. Connecting four spare clients first puts these
    // traders on ids 5 and 6, so the two classes never cancel each other's trade.
    private const int Spare = 4;

    private static readonly ItemData[] Templates =
    [
        new() { ID = Single, Stack_Max = 1, Is_Trade = 1 },
        new() { ID = SingleOther, Stack_Max = 1, Is_Trade = 1 },
        new() { ID = Stackable, Stack_Max = 120, Is_Trade = 1 },
    ];

    // ---------- world ----------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Forwards everything to <paramref name="inner"/> but throws on the N-th atomic owner flip.</summary>
    private sealed class FaultingInventory(IInventoryRepository inner, int failOnReassign) : IInventoryRepository
    {
        private int _reassigns;
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => inner.GetByCharacterAsync(characterId);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => inner.GetByIdAsync(id);
        public Task AddItemAsync(InventoryItem item) => inner.AddItemAsync(item);
        public Task RemoveItemAsync(ulong itemId) => inner.RemoveItemAsync(itemId);
        public Task UpdateItemAsync(InventoryItem item) => inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => inner.RepairSlotsAsync(characterId);
        public Task<bool> RemoveItemAsync(ulong itemId, uint characterId) => inner.RemoveItemAsync(itemId, characterId);
        public Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta) => inner.AdjustCountAsync(itemId, characterId, delta);
        public Task<bool> EnsureIndexesAsync() => inner.EnsureIndexesAsync();

        public Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
        {
            if (Interlocked.Increment(ref _reassigns) == failOnReassign)
                throw new InvalidOperationException("injected fault on owner flip " + failOnReassign);
            return inner.TryReassignAsync(itemId, fromCharId, toCharId, toStorageType, toSlot);
        }
    }

    private sealed class World
    {
        public required EconomyFixture Fx { get; init; }
        public required PlayerState A { get; init; }
        public required PlayerState B { get; init; }
        public required Harness Net { get; init; }
        public required PlayerStateManager Players { get; init; }
        public required BroadcastService Broadcast { get; init; }
        public required CapturingLogger<TradeConfirmHandler> ConfirmLog { get; init; }
        public required TradeConfirmHandler Confirm { get; init; }
        public required TradeMoveHandler Move { get; init; }
        public required TradeDelHandler Del { get; init; }
        public required TradeMoneyHandler Money { get; init; }

        public ProudSession SessA => Net.Sessions[Spare];
        public ProudSession SessB => Net.Sessions[Spare + 1];
        public FrameReader ReaderA => Net.Readers[Spare];
        public FrameReader ReaderB => Net.Readers[Spare + 1];

        public static World Create(Harness net, long goldA = 0, long goldB = 0,
            Func<IInventoryRepository, IInventoryRepository>? wrapInventory = null, ItemData[]? templates = null)
        {
            var fx = new EconomyFixture();
            var a = fx.AddPlayer(CharA, goldA);
            var b = fx.AddPlayer(CharB, goldB);
            a.SessionId = net.Sessions[Spare].SessionId; a.EntityId = HandleA;
            b.SessionId = net.Sessions[Spare + 1].SessionId; b.EntityId = HandleB;

            var players = new PlayerStateManager(new NoDirtySink());
            players.AddPlayer(a.SessionId, a);
            players.AddPlayer(b.SessionId, b);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);

            var store = new GameDataStore();
            typeof(GameDataStore).GetProperty(nameof(GameDataStore.Items))!
                .SetValue(store, (templates ?? Templates).ToFrozenDictionary(t => t.ID));
            var inventory = wrapInventory?.Invoke(fx.InventoryRepo) ?? fx.InventoryRepo;
            var sf = new ServiceCollection()
                .AddScoped(_ => inventory)
                .AddScoped<ICharacterRepository>(_ => fx.CharacterRepo)
                .AddSingleton(store)
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();

            var confirmLog = new CapturingLogger<TradeConfirmHandler>();
            return new World
            {
                Fx = fx, A = a, B = b, Net = net, Players = players, Broadcast = broadcast, ConfirmLog = confirmLog,
                Confirm = new TradeConfirmHandler(sf, confirmLog, players, broadcast),
                Move = new TradeMoveHandler(sf, NullLogger<TradeMoveHandler>.Instance, players, broadcast),
                Del = new TradeDelHandler(sf, NullLogger<TradeDelHandler>.Instance, players, broadcast),
                Money = new TradeMoneyHandler(sf, NullLogger<TradeMoneyHandler>.Instance, players, broadcast),
            };
        }

        /// <summary>A fresh live trade between the two traders.</summary>
        public TradeSession OpenTrade()
        {
            TradeSessionService.Instance.Cancel(A.SessionId);
            TradeSessionService.Instance.Cancel(B.SessionId);
            var request = TradeSessionService.Instance.Request(A.SessionId, B.SessionId, HandleA, HandleB, _ => true);
            Assert.True(TradeSessionService.Instance.TryAccept(request));
            return request;
        }

        public Task OfferAsync(PlayerState who, InventoryItem item, int qty) =>
            Move.HandleAsync(who == A ? SessA : SessB, Move1353(item.Id, item.ItemId, qty));

        public Task OfferGoldAsync(PlayerState who, ulong gold) =>
            Money.HandleAsync(who == A ? SessA : SessB, Money1355(gold));

        /// <summary>Both sides lock, B confirms through the session API and A's confirm goes through the handler,
        /// which latches and runs the swap.</summary>
        public Task LockAndConfirmAsync(TradeSession trade)
        {
            trade.SetLocked(A.SessionId, true);
            trade.SetLocked(B.SessionId, true);
            Assert.False(trade.ConfirmAndTryComplete(B.SessionId));
            return Confirm.HandleAsync(SessA, Confirm1357());
        }

        public void Close() => TradeSessionService.Instance.Cancel(A.SessionId);

        public InventoryItem Row(ulong id) => Fx.Inventory.Rows.Single(r => r.Id == id);

        /// <summary>Seeds a bag row under a chosen, recognisable uid (so a log line can be matched to it).</summary>
        public InventoryItem Give(uint characterId, uint itemId, ulong uid, short slot, int count = 1)
        {
            Fx.Inventory.Seed(new InventoryItem { Id = uid, CharacterId = characterId, ItemId = itemId, Count = count, StorageType = 0, Slot = slot });
            return Row(uid);
        }
    }

    // ---------- wire ----------

    private static byte[] Confirm1357() { var p = new byte[11]; p[10] = 1; return p; }

    private static byte[] Move1353(ulong uid, uint itemId, int qty)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(18), itemId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), (ushort)qty);
        return p;
    }

    private static byte[] Del1354(ulong uid)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), uid);
        return p;
    }

    private static byte[] Money1355(ulong gold)
    {
        var p = new byte[18];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), gold);
        return p;
    }

    /// <summary>Reads frames until one with this opcode and exactly this body arrives.</summary>
    private static async Task ExpectFrameAsync(FrameReader reader, ushort op, byte[] body, int maxFrames = 60)
    {
        var seen = new List<string>();
        for (var i = 0; i < maxFrames; i++)
        {
            var (gotOp, gotBody) = await reader.NextAsync();
            if (gotOp == op && gotBody.AsSpan().SequenceEqual(body)) return;
            seen.Add($"{gotOp}/{gotBody.Length}B");
        }
        throw new Xunit.Sdk.XunitException($"no frame {op} with the expected {body.Length}-byte body in {maxFrames} frames: {string.Join(" ", seen)}");
    }

    private static async Task<Harness> StartAsync() => await Harness.StartAsync(Spare + 2);

    // ---------- E-H3: the swap ----------

    [Fact]
    public async Task Swap_WholeStacks_FlipAtomically_ZenyMovesAfterTheItems_InvariantsHold()
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000, goldB: 500);
        var x = w.Fx.GiveItem(CharA, Single);
        var y = w.Fx.GiveItem(CharB, Single);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, x, 1);
        await w.OfferAsync(w.B, y, 1);
        await w.OfferGoldAsync(w.A, 300);
        await w.OfferGoldAsync(w.B, 100);
        w.Fx.Scheduler.ClearTrace();

        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharB, w.Row(x.Id).CharacterId);   // same uid, now B's
        Assert.Equal(CharA, w.Row(y.Id).CharacterId);
        Assert.Equal((800L, 700L), (w.A.Gold, w.B.Gold));
        Assert.Equal((800L, 700L), (w.Fx.Characters.Rows.Single(c => c.Id == CharA).Gold, w.Fx.Characters.Rows.Single(c => c.Id == CharB).Gold));
        var trace = w.Fx.Scheduler.Trace;
        Assert.Equal(2, trace.Count(t => t.EndsWith("Inventory.TryReassign")));   // one atomic flip per item...
        Assert.DoesNotContain(trace, t => t.EndsWith("Inventory.RemoveOwned") || t.EndsWith("Inventory.Add"));   // ...and no remove+insert
        // the item flips come before the wallet write-through
        Assert.True(trace.ToList().FindLastIndex(t => t.EndsWith("Inventory.TryReassign"))
                    < trace.ToList().FindIndex(t => t.EndsWith("Character.UpdateGold")));
        Assert.DoesNotContain(w.ConfirmLog.Entries, e => e.Level >= LogLevel.Warning && !e.Message.StartsWith("Khara"));   // Khara: this DI has no KharaService deps
        w.Close();
    }

    [Fact]
    public async Task Swap_FaultOnTheSecondFlip_EachItemIsWhollyWithOneSide_WalletsAreRefunded_LoggedAtError()
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000, goldB: 500, wrapInventory: inner => new FaultingInventory(inner, failOnReassign: 2));
        var x = w.Give(CharA, Single, 0x0E043DA800AA0001UL, slot: 0);
        var y = w.Give(CharB, Single, 0x0E043DA800AA0002UL, slot: 0);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, x, 1);
        await w.OfferAsync(w.B, y, 1);
        await w.OfferGoldAsync(w.A, 300);
        await w.OfferGoldAsync(w.B, 100);

        // The first flip (A's item to B) commits, the second (B's item to A) throws: each uid is still in exactly one
        // place, no zeny was minted or lost, and nobody was left holding an escrow.
        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharB, w.Row(x.Id).CharacterId);
        Assert.Equal(CharB, w.Row(y.Id).CharacterId);
        Assert.Equal((1000L, 500L), (w.A.Gold, w.B.Gold));
        var error = Assert.Single(w.ConfirmLog.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(trade.Id.ToString(), error.Message);
        Assert.Contains(y.Id.ToString("X"), error.Message);     // the uid that was not yet moved
        Assert.DoesNotContain(x.Id.ToString("X"), error.Message);
        Assert.Contains("300", error.Message);
        Assert.Contains("100", error.Message);
        await ExpectFrameAsync(w.ReaderA, ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(HandleA, 0));
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(HandleB, 0));
        Assert.Null(TradeSessionService.Instance.Get(w.A.SessionId));
    }

    [Fact]
    public async Task Swap_FaultOnTheFirstFlip_NothingMoves_WalletsRefunded_WarningNotError()
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000, goldB: 500, wrapInventory: inner => new FaultingInventory(inner, failOnReassign: 1));
        var x = w.Fx.GiveItem(CharA, Single);
        var y = w.Fx.GiveItem(CharB, Single);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, x, 1);
        await w.OfferAsync(w.B, y, 1);
        await w.OfferGoldAsync(w.A, 300);
        await w.OfferGoldAsync(w.B, 100);

        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharA, w.Row(x.Id).CharacterId);
        Assert.Equal(CharB, w.Row(y.Id).CharacterId);
        Assert.Equal((1000L, 500L), (w.A.Gold, w.B.Gold));
        Assert.DoesNotContain(w.ConfirmLog.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(w.ConfirmLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("before any item moved"));
        await ExpectFrameAsync(w.ReaderA, ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(HandleA, 0));
        w.Close();
    }

    [Fact]
    public async Task Swap_MergeIntoHeldStack_And_PartialMergeOfAWholeStack_ConserveUnits()
    {
        await using var net = await StartAsync();
        var w = World.Create(net);
        var held = w.Fx.GiveItem(CharB, Stackable, count: 50);       // B already holds 50 of 120
        var whole = w.Fx.GiveItem(CharA, Stackable, count: 100);     // A offers the whole 100: 70 merge, 30 take a cell
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, whole, 100);

        var (_, after) = await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(120, w.Row(held.Id).Count);                     // topped up to Stack_Max
        var moved = w.Fx.Inventory.Rows.Single(r => r.Id == whole.Id);
        Assert.Equal((CharB, 30), (moved.CharacterId, moved.Count)); // the remainder kept its uid and moved owner
        Assert.Equal(150, after.Items.Where(i => i.ItemId == Stackable).Sum(i => i.Count));
        w.Close();
    }

    [Fact]
    public async Task Swap_PartialSplit_StillConservesUnits()
    {
        await using var net = await StartAsync();
        var w = World.Create(net);
        var stack = w.Fx.GiveItem(CharA, Stackable, count: 10);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, stack, 5);

        var before = w.Fx.Snapshot();
        await w.LockAndConfirmAsync(trade);
        var after = w.Fx.Snapshot();
        EconomyFixture.CheckAll(before, after, 0, new ItemExpectation
        {
            CreatedUids = after.Items.Select(i => i.Uid).Except(before.Items.Select(i => i.Uid)).ToHashSet(),
        });
        Assert.Equal(5, w.Row(stack.Id).Count);
        Assert.Equal(5, w.Fx.Inventory.Rows.Where(r => r.CharacterId == CharB).Sum(r => r.Count));
        w.Close();
    }

    // ---------- E-H3: interleavings against a competing sale ----------

    private const int OfferedUnits = 5;
    private const int StackUnits = 10;

    private static IEnumerable<string> SingleCompetitorSchedules(int length) =>
        Enumerable.Range(0, length + 1).Select(k => new string('0', k) + "1");

    /// <summary>Partial 5-of-10 offer vs a competitor that sells the whole stack by a guarded decrement of 10 (a
    /// competitor that does not take the inventory gate, i.e. the cross-process backstop): at EVERY position of the
    /// competitor among the swap's repository calls, the 10 units are accounted for exactly once.</summary>
    [Fact]
    public async Task Partial5of10_VsUngatedFullDecrement_AtEveryInterleaving_NoMint_InvariantB()
    {
        await using var net = await StartAsync();
        var runs = 0;
        foreach (var schedule in SingleCompetitorSchedules(45))
        {
            var w = World.Create(net);
            var stack = w.Fx.GiveItem(CharA, Stackable, count: StackUnits);
            var trade = w.OpenTrade();
            await w.OfferAsync(w.A, stack, OfferedUnits);
            trade.SetLocked(w.A.SessionId, true);
            trade.SetLocked(w.B.SessionId, true);
            Assert.False(trade.ConfirmAndTryComplete(w.B.SessionId));

            var sold = 0;
            var before = w.Fx.Snapshot();
            var result = await w.Fx.RunInterleavedAsync(
            [
                () => w.Confirm.HandleAsync(w.SessA, Confirm1357()),
                async () => { if (await w.Fx.InventoryRepo.AdjustCountAsync(stack.Id, CharA, -StackUnits) is not null) sold = StackUnits; },
            ], schedule);
            result.ThrowIfFaulted();
            var after = w.Fx.Snapshot();

            var created = after.Items.Select(i => i.Uid).Except(before.Items.Select(i => i.Uid)).ToHashSet();
            EconomyFixture.CheckAll(before, after, 0, new ItemExpectation
            {
                CreatedUids = created,
                QuantityDelta = new Dictionary<uint, long> { [Stackable] = -sold },
            });
            Assert.True(w.Fx.Inventory.Rows.Where(r => r.CharacterId == CharB).Sum(r => r.Count) <= OfferedUnits,
                $"schedule {schedule}: B received more than was offered");
            w.Close();
            runs++;
        }
        Assert.Equal(46, runs);
    }

    /// <summary>Same offer vs the competitor deleting the stack's row under an owner filter and NO gate (the other
    /// store-level sale shape): units may be sold or delivered, never copied.</summary>
    [Fact]
    public async Task Partial5of10_VsUngatedRowDelete_AtEveryInterleaving_NeverMintsUnits()
    {
        await using var net = await StartAsync();
        foreach (var schedule in SingleCompetitorSchedules(45))
        {
            var w = World.Create(net);
            var stack = w.Fx.GiveItem(CharA, Stackable, count: StackUnits);
            var trade = w.OpenTrade();
            await w.OfferAsync(w.A, stack, OfferedUnits);
            trade.SetLocked(w.A.SessionId, true);
            trade.SetLocked(w.B.SessionId, true);
            Assert.False(trade.ConfirmAndTryComplete(w.B.SessionId));

            var result = await w.Fx.RunInterleavedAsync(
            [
                () => w.Confirm.HandleAsync(w.SessA, Confirm1357()),
                async () => { await w.Fx.InventoryRepo.RemoveItemAsync(stack.Id, CharA); },
            ], schedule);
            result.ThrowIfFaulted();

            var rows = w.Fx.Inventory.Rows;
            Assert.True(rows.Sum(r => r.Count) <= StackUnits, $"schedule {schedule}: {rows.Sum(r => r.Count)} units out of {StackUnits}");
            Assert.True(rows.Where(r => r.CharacterId == CharB).Sum(r => r.Count) <= OfferedUnits, $"schedule {schedule}");
            Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
            EconomyInvariants.AssertUniquePlacement(w.Fx.Snapshot());
            w.Close();
        }
    }

    /// <summary>Whole-stack offer vs a competitor that sells it under the character's inventory gate, started both
    /// before and after the swap: exactly one of them takes the item.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WholeStack_VsGatedSale_ExactlyOneWins_InvariantB(bool saleStartsFirst)
    {
        await using var net = await StartAsync();
        foreach (var schedule in new[] { "", "1", "01", "0011", "000111", "0101", "1010" })
        {
            var w = World.Create(net);
            var item = w.Fx.GiveItem(CharA, Single);
            var trade = w.OpenTrade();
            await w.OfferAsync(w.A, item, 1);
            trade.SetLocked(w.A.SessionId, true);
            trade.SetLocked(w.B.SessionId, true);
            Assert.False(trade.ConfirmAndTryComplete(w.B.SessionId));

            var sold = 0;
            Func<Task> swap = () => w.Confirm.HandleAsync(w.SessA, Confirm1357());
            Func<Task> sale = () => CharacterInventoryGate.RunAsync(CharA, async () =>
            {
                var row = await w.Fx.InventoryRepo.GetByIdAsync(item.Id);
                if (row is not null && row.CharacterId == CharA && await w.Fx.InventoryRepo.RemoveItemAsync(item.Id, CharA))
                    sold = row.Count;
                return 0;
            });

            var before = w.Fx.Snapshot();
            var result = await w.Fx.RunInterleavedAsync(saleStartsFirst ? [sale, swap] : [swap, sale], schedule);
            result.ThrowIfFaulted();
            var after = w.Fx.Snapshot();

            EconomyFixture.CheckAll(before, after, 0, new ItemExpectation
            {
                ConsumedUids = new HashSet<ulong> { item.Id },
                QuantityDelta = new Dictionary<uint, long> { [Single] = -sold },
            });
            var rows = w.Fx.Inventory.Rows.Where(r => r.Id == item.Id).ToList();
            Assert.True(rows.Count == 1 ^ sold == 1, $"schedule {schedule}: exactly one path must take the item (rows {rows.Count}, sold {sold})");
            w.Close();
        }
    }

    // ---------- E-H4: offer binding ----------

    public static TheoryData<string> Mutations =>
    [
        "template", "refine", "element", "bound", "dye1", "dye2", "dye3", "fx", "enchantId", "enchantSeconds", "cards", "punchers",
    ];

    private static void Mutate(InventoryItem row, string what)
    {
        switch (what)
        {
            case "template": row.ItemId = SingleOther; break;
            case "refine": row.Refine = 7; break;
            case "element": row.Element = 3; break;
            case "bound": row.Bound = true; break;
            case "dye1": row.DyeColor = 0xFF272725; break;
            case "dye2": row.DyeColor2 = 0xFF272725; break;
            case "dye3": row.DyeColor3 = 0xFF272725; break;
            case "fx": row.FxEnchantIndex = 204; break;
            case "enchantId": row.EnchantReinforceId = 9; break;
            case "enchantSeconds": row.EnchantSeconds = 600; break;
            case "cards": row.CardsJson = "[22320029]"; break;
            case "punchers": row.SocketPunchersJson = "[33000601]"; break;
            default: throw new ArgumentOutOfRangeException(nameof(what));
        }
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task OfferThenMutateTheRowUnderTheSameUid_AbortsTheSwap_NothingMoves(string what)
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000, goldB: 500);
        var x = w.Fx.GiveItem(CharA, Single);
        var y = w.Fx.GiveItem(CharB, Single);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, x, 1);
        await w.OfferAsync(w.B, y, 1);
        await w.OfferGoldAsync(w.A, 300);

        // The offered row is replaced under the same uid by a different instance (what a sale + a uid reuse leaves).
        var live = w.Row(x.Id);
        Mutate(live, what);
        await w.Fx.InventoryRepo.UpdateItemAsync(live);

        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharA, w.Row(x.Id).CharacterId);
        Assert.Equal(CharB, w.Row(y.Id).CharacterId);
        Assert.Equal((1000L, 500L), (w.A.Gold, w.B.Gold));
        Assert.Contains(w.ConfirmLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("aborted: ownership"));
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(HandleB, 0));
        w.Close();
    }

    [Fact]
    public async Task OfferWithNoChange_Completes()
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000, goldB: 500);
        var x = w.Fx.GiveItem(CharA, Single, count: 1);
        var trade = w.OpenTrade();
        await w.OfferAsync(w.A, x, 1);

        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharB, w.Row(x.Id).CharacterId);
        w.Close();
    }

    [Theory]
    [InlineData((byte)0, (byte)0)]   // Is_Trade 0
    [InlineData((byte)1, (byte)2)]   // Bind_Type 2 (bound on pickup)
    public async Task SwapReRunsTheTradeableGate_OnTheLiveRow(byte isTrade, byte bindType)
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 1000,
            templates: [new ItemData { ID = Single, Stack_Max = 1, Is_Trade = isTrade, Bind_Type = bindType }]);
        var item = w.Fx.GiveItem(CharA, Single);
        // Offered with no snapshot (only TradeMoveHandler takes one) so the gate is the only thing that can refuse.
        var trade = w.OpenTrade();
        Assert.Equal(TradeOfferChange.Applied, trade.AddItem(w.A.SessionId, item.Id, 1));

        await w.Fx.AssertInvariantsAsync(() => w.LockAndConfirmAsync(trade));

        Assert.Equal(CharA, w.Row(item.Id).CharacterId);
        w.Close();
    }

    // ---------- E-H9: 1354 leaks nothing ----------

    [Fact]
    public async Task TradeDel_OfAnotherPlayersUid_Sends_TheZeroRecord()
    {
        await using var net = await StartAsync();
        var w = World.Create(net);
        var secret = w.Fx.GiveItem(CharB, Single);
        secret.Refine = 9;
        secret.CardsJson = "[22320029]";
        await w.Fx.InventoryRepo.UpdateItemAsync(secret);
        w.OpenTrade();

        await w.Del.HandleAsync(w.SessA, Del1354(secret.Id));

        var zero = TradePackets.BuildOnDelTradePC(HandleA, new byte[126]);
        await ExpectFrameAsync(w.ReaderA, ZoneS2COpcodes.OnDelTradePC, zero);
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnDelTradePC, zero);
        w.Close();
    }

    [Fact]
    public async Task TradeDel_OfAUidNotOnTheTable_EvenYourOwn_SendsTheZeroRecord_AndOfTheOfferedOne_TheRealRecord()
    {
        await using var net = await StartAsync();
        var w = World.Create(net);
        var offered = w.Fx.GiveItem(CharA, Single);
        var notOffered = w.Fx.GiveItem(CharA, Single);
        w.OpenTrade();
        await w.OfferAsync(w.A, offered, 1);

        await w.Del.HandleAsync(w.SessA, Del1354(notOffered.Id));
        await ExpectFrameAsync(w.ReaderA, ZoneS2COpcodes.OnDelTradePC, TradePackets.BuildOnDelTradePC(HandleA, new byte[126]));

        await w.Del.HandleAsync(w.SessA, Del1354(offered.Id));
        var real = TradePackets.BuildOnDelTradePC(HandleA, WorldEntryService.BuildItemRecord126(w.Row(offered.Id)));
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnDelTradePC, real);
        w.Close();
    }

    // ---------- E-H10: 1355 is clamped to the wallet ----------

    [Theory]
    [InlineData(1_000_000_000_000UL, 500L)]
    [InlineData(ulong.MaxValue, 500L)]
    [InlineData(501UL, 500L)]
    [InlineData(500UL, 500L)]
    [InlineData(200UL, 200L)]
    [InlineData(0UL, 0L)]
    public async Task TradeMoney_OfferIsClampedToTheWallet_AndTheEchoCarriesTheClampedAmount(ulong offer, long expected)
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 500);
        var trade = w.OpenTrade();

        await w.OfferGoldAsync(w.A, offer);

        Assert.Equal(expected, trade.OfferFor(w.A.SessionId).Gold);
        var echo = TradePackets.BuildOnDragGold(HandleA, (ulong)expected);
        await ExpectFrameAsync(w.ReaderA, ZoneS2COpcodes.OnDragGold, echo);
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnDragGold, echo);
        w.Close();
    }

    // ---------- E-H8: a disconnect ends the trade ----------

    [Fact]
    public async Task Disconnect_CancelsTheLiveTrade_AndClosesThePartnersWindow()
    {
        await using var net = await StartAsync();
        var w = World.Create(net, goldA: 500);
        var trade = w.OpenTrade();
        await w.OfferGoldAsync(w.A, 100);
        var zone = new ZoneService(w.Players, w.Broadcast, NullLogger<ZoneService>.Instance);

        await zone.HandlePlayerDisconnectAsync(w.A.SessionId);

        Assert.Null(TradeSessionService.Instance.Get(w.A.SessionId));
        Assert.Null(TradeSessionService.Instance.Get(w.B.SessionId));
        Assert.False(trade.Completing);
        await ExpectFrameAsync(w.ReaderB, ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(HandleB, 0));
        Assert.Equal(500L, w.A.Gold);   // nothing was escrowed, nothing to give back
    }

    [Fact]
    public async Task Disconnect_WithNoTrade_IsHarmless()
    {
        await using var net = await StartAsync();
        var w = World.Create(net);
        var zone = new ZoneService(w.Players, w.Broadcast, NullLogger<ZoneService>.Instance);
        TradeSessionService.Instance.Cancel(w.A.SessionId);
        TradeSessionService.Instance.Cancel(w.B.SessionId);

        await zone.HandlePlayerDisconnectAsync(w.A.SessionId);

        Assert.Null(TradeSessionService.Instance.Get(w.B.SessionId));
    }
}
