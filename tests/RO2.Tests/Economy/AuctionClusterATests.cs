using RO2.Economy.Domain;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Economy;

/// <summary>
/// Economy hardening, cluster A (auction): the 1403 hours whitelist (E-H11a), the 1406 refusal answers (E-H11b),
/// the sell-start take under the inventory gate (E-H11c), the trade/bind gate on listings (E-H5) and the NPC gate
/// on 1399 (E-H8). Runs on <see cref="EconomyFixture"/>: invariant A (zeny conservation) and D (no negative gold)
/// are asserted on every state-changing path, B (items) wherever an item moves.
/// </summary>
public class AuctionClusterATests
{
    private const uint SellerId = 1;
    private const uint BuyerId = 2;
    private const uint TemplateId = 5001;

    // ---------------------------------------------------------------- setup

    private static (AuctionService Svc, ServiceProvider Sp) Build(EconomyFixture fx, IAuctionRepository? auction = null,
        NpcStateManager? npcs = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IAuctionRepository>(auction ?? fx.AuctionRepo);
        sc.AddSingleton<IMailRepository>(fx.MailRepo);
        sc.AddSingleton<ICharacterRepository>(fx.CharacterRepo);
        sc.AddSingleton<IInventoryRepository>(fx.InventoryRepo);
        if (npcs != null) sc.AddSingleton(npcs);
        var sp = sc.BuildServiceProvider();
        var pm = new PlayerStateManager(new NoDirtySink());
        var bc = new BroadcastService(pm, NullLogger<BroadcastService>.Instance);
        return (AuctionService.From(sp, pm, bc, NullLogger.Instance), sp);
    }

    private static AuctionPackets.SellStartRequest Sell(ulong itemUid, byte count = 1, byte hours = 24,
        ulong start = 100, ulong buyout = 1000)
        => new(itemUid, 0, 0, count, hours, start, buyout, 0x13);

    private static Task<AuctionService.SellStartOutcome> StartSale(AuctionService svc, EconomyFixture fx, PlayerState seller,
        AuctionPackets.SellStartRequest req)
        => svc.StartSaleAsync(seller, req, fx.InventoryRepo, fx.CharacterRepo, new GameDataStore());

    // ---------------------------------------------------------------- E-H11a / E-H5: CheckSellStart

    private static AuctionPackets.SellStartRequest Req(byte hours)
        => new(1, 0, 0, 1, hours, 100, 1000, 0x13);

    private static InventoryItem Bagged() => new() { Id = 1, CharacterId = SellerId, ItemId = TemplateId, Count = 5, StorageType = 0 };

    private static ushort Check(AuctionPackets.SellStartRequest req, InventoryItem? item = null, ItemData? template = null)
        => AuctionService.CheckSellStart(req, item ?? Bagged(), SellerId, long.MaxValue, 1, 1_000_000, 0, template);

    [Theory]
    [InlineData((byte)24)]
    [InlineData((byte)72)]
    [InlineData((byte)168)]
    public void Hours_InTheWhitelist_AreAccepted(byte hours) => Assert.Equal((ushort)0, Check(Req(hours)));

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)23)]
    [InlineData((byte)25)]
    [InlineData((byte)48)]
    [InlineData((byte)167)]
    [InlineData((byte)169)]
    [InlineData((byte)255)]
    public void Hours_OutsideTheWhitelist_AreRefusedWith16304(byte hours)
        => Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(Req(hours)));

    [Fact]
    public async Task StartSale_Hours72_Lists_Hours255_IsRefusedAndChangesNothing()
    {
        var fx = new EconomyFixture();
        var seller = fx.AddPlayer(SellerId, gold: 1000);
        var item = fx.GiveItem(SellerId, TemplateId);
        var (svc, _) = Build(fx);

        var (_, afterRefusal) = await fx.AssertInvariantsAsync(async () =>
            Assert.Equal(AuctionPackets.ResultSellItemMismatch, (await StartSale(svc, fx, seller, Sell(item.Id, hours: 255))).Result));
        Assert.Equal(1000, seller.Gold);
        Assert.Single(afterRefusal.Items);
        Assert.Empty(fx.Auction.Articles);

        // A fee of 1 (no template in the empty store) leaves the economy: the declared sink.
        AuctionService.SellStartOutcome ok = default;
        await fx.AssertInvariantsAsync(async () => ok = await StartSale(svc, fx, seller, Sell(item.Id, hours: 72)), zenyDelta: -1);
        Assert.Equal((ushort)0, ok.Result);
        var article = Assert.Single(fx.Auction.Articles);
        Assert.Equal(TimeSpan.FromHours(72), article.ExpiresAt - article.StartedAt);
        Assert.Equal(999, seller.Gold);
        Assert.Empty(fx.Inventory.Rows);
    }

    [Fact]
    public void NonTradeableOrBoundItems_AreRefusedWith16304_TradeableOnesPass()
    {
        Assert.Equal((ushort)0, Check(Req(24), template: new ItemData { ID = TemplateId, Is_Trade = 1, Bind_Type = 0 }));
        Assert.Equal((ushort)0, Check(Req(24), template: null)); // no template known: the gate cannot judge
        Assert.Equal(AuctionPackets.ResultSellItemMismatch,
            Check(Req(24), template: new ItemData { ID = TemplateId, Is_Trade = 0, Bind_Type = 0 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch,
            Check(Req(24), template: new ItemData { ID = TemplateId, Is_Trade = 1, Bind_Type = 2 }));
        // Bind on equip: bound once worn (Bound flag), tradeable before.
        Assert.Equal((ushort)0, Check(Req(24), template: new ItemData { ID = TemplateId, Is_Trade = 1, Bind_Type = 1 }));
        var worn = Bagged();
        worn.Bound = true;
        Assert.Equal(AuctionPackets.ResultSellItemMismatch,
            Check(Req(24), worn, new ItemData { ID = TemplateId, Is_Trade = 1, Bind_Type = 1 }));
    }

    // ---------------------------------------------------------------- E-H11c: the take under the gate

    private static EconomyFixture RaceWorld(int count = 1)
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(SellerId, gold: 1000);
        fx.GiveItem(SellerId, TemplateId, count);
        return fx;
    }

    [Theory]
    [InlineData("01010101010101010101010101010101")]
    [InlineData("0011001100110011")]
    [InlineData("00000000000000000000010101010101")]
    [InlineData("1010101010101010")]
    public async Task DoubleSentSellStart_ListsTheItemOnce(string schedule)
    {
        var fx = RaceWorld();
        var seller = fx.Players[SellerId];
        var item = fx.Inventory.Rows.Single();
        var (svc, _) = Build(fx);
        var outcomes = new AuctionService.SellStartOutcome[2];
        var before = fx.Snapshot();

        var run = await fx.RunInterleavedAsync(
            [async () => outcomes[0] = await StartSale(svc, fx, seller, Sell(item.Id)),
             async () => outcomes[1] = await StartSale(svc, fx, seller, Sell(item.Id))], schedule);
        run.ThrowIfFaulted();

        Assert.Equal(1, outcomes.Count(o => o.Result == 0));
        Assert.Equal(1, outcomes.Count(o => o.Result == AuctionPackets.ResultSellItemMismatch));
        Assert.Single(fx.Auction.Articles);
        Assert.Empty(fx.Inventory.Rows);
        Assert.Equal(999, seller.Gold); // one fee: the loser's was handed back
        EconomyFixture.CheckAll(before, fx.Snapshot(), zenyDelta: -1);
        EconomyInvariants.AssertNoNegativeWalletFault(run);
    }

    [Fact]
    public async Task DoubleSentSellStart_OfAPartialStack_NeverListsMoreThanTheBagHolds()
    {
        var fx = RaceWorld(count: 5);
        var seller = fx.Players[SellerId];
        var item = fx.Inventory.Rows.Single();
        var (svc, _) = Build(fx);
        var outcomes = new AuctionService.SellStartOutcome[2];
        var before = fx.Snapshot();

        var run = await fx.RunInterleavedAsync(
            [async () => outcomes[0] = await StartSale(svc, fx, seller, Sell(item.Id, count: 4)),
             async () => outcomes[1] = await StartSale(svc, fx, seller, Sell(item.Id, count: 4))],
            "01010101010101010101010101010101");
        run.ThrowIfFaulted();

        var article = Assert.Single(fx.Auction.Articles); // 4 + 4 > 5: only one fits
        Assert.Equal(4, article.Count);
        Assert.Equal(1, Assert.Single(fx.Inventory.Rows).Count);
        Assert.Equal(1, outcomes.Count(o => o.Result == 0));
        // The listed part got a uid of its own: the only created one. Quantity of the template is conserved.
        EconomyFixture.CheckAll(before, fx.Snapshot(), zenyDelta: -1,
            new ItemExpectation { CreatedUids = new HashSet<ulong> { article.ItemUid } });
        EconomyInvariants.AssertNoNegativeWalletFault(run);
    }

    [Theory]
    [InlineData("01010101010101010101010101010101")]
    [InlineData("10101010101010101010101010101010")]
    [InlineData("00000011111111111111111111111111")]
    [InlineData("0000000000000000000000000000000000000000")] // the listing finishes first: the sale finds nothing
    [InlineData("0001001001001001001001001001001001001001")]
    public async Task SellStartRacingASale_TheItemLeavesTheBagOnce(string schedule)
    {
        var fx = RaceWorld();
        var seller = fx.Players[SellerId];
        var item = fx.Inventory.Rows.Single();
        var (svc, _) = Build(fx);
        AuctionService.SellStartOutcome outcome = default;
        var sold = false;
        var before = fx.Snapshot();

        // The NPC sale's take (SellItemHandler): read and owner-filtered delete under the same gate.
        var run = await fx.RunInterleavedAsync(
            [async () => outcome = await StartSale(svc, fx, seller, Sell(item.Id)),
             async () => sold = await CharacterInventoryGate.RunAsync(SellerId, async () =>
                 await fx.InventoryRepo.GetByIdAsync(item.Id) is not null && await fx.InventoryRepo.RemoveItemAsync(item.Id, SellerId))],
            schedule);
        run.ThrowIfFaulted();

        var listed = fx.Auction.Articles.Count;
        Assert.Equal(1, listed + (sold ? 1 : 0)); // sold once: listed XOR sold to the NPC
        Assert.Empty(fx.Inventory.Rows);
        if (sold)
        {
            Assert.Equal(AuctionPackets.ResultSellItemMismatch, outcome.Result);
            Assert.Equal(1000, seller.Gold); // the fee came back
            EconomyFixture.CheckAll(before, fx.Snapshot(), 0, new ItemExpectation
            {
                ConsumedUids = new HashSet<ulong> { item.Id },
                QuantityDelta = new Dictionary<uint, long> { [TemplateId] = -1 },
            });
        }
        else
        {
            Assert.Equal((ushort)0, outcome.Result);
            EconomyFixture.CheckAll(before, fx.Snapshot(), -1);
        }
        EconomyInvariants.AssertNoNegativeWalletFault(run);
    }

    [Fact]
    public async Task SellStartExploration_DoubleSend_HoldsInvariantsOnEverySampledInterleaving()
    {
        var summary = await RepoScheduler.ExploreAllAsync(
            create: () => RaceWorld(),
            ops: fx =>
            {
                var (svc, _) = Build(fx);
                var seller = fx.Players[SellerId];
                var uid = fx.Inventory.Rows.Single().Id;
                return [() => StartSale(svc, fx, seller, Sell(uid)), () => StartSale(svc, fx, seller, Sell(uid))];
            },
            verify: (fx, result) =>
            {
                result.ThrowIfFaulted();
                if (fx.Auction.Articles.Count != 1) throw new EconomyInvariantViolation($"{fx.Auction.Articles.Count} articles for one item");
                if (fx.Inventory.Rows.Count != 0) throw new EconomyInvariantViolation("the item is in the bag and on sale");
                if (fx.Players[SellerId].Gold != 999) throw new EconomyInvariantViolation($"wallet {fx.Players[SellerId].Gold}, expected 999");
                return Task.CompletedTask;
            },
            maxRuns: 30);
        Assert.True(summary.Runs > 0);
    }

    // ---- the restore after a failed insert (the part that now uses the guarded calls) ----

    public class ThrowOnInsert : DispatchProxy
    {
        public IAuctionRepository Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAuctionRepository.InsertAsync)) throw new InvalidOperationException("insert failed");
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
    }

    private static IAuctionRepository FailingInsert(EconomyFixture fx)
    {
        var proxy = DispatchProxy.Create<IAuctionRepository, ThrowOnInsert>();
        ((ThrowOnInsert)(object)proxy).Inner = fx.AuctionRepo;
        return proxy;
    }

    [Theory]
    [InlineData(5, 5)] // a whole stack is put back with its own id and slot
    [InlineData(5, 2)] // a part is added back onto the stack that stayed
    public async Task FailedInsert_PutsTheItemBack_AndHandsTheFeeBack(int stack, int listed)
    {
        var fx = RaceWorld(stack);
        var seller = fx.Players[SellerId];
        var item = fx.Inventory.Rows.Single();
        var (svc, _) = Build(fx, FailingInsert(fx));

        AuctionService.SellStartOutcome o = default;
        await fx.AssertInvariantsAsync(async () => o = await StartSale(svc, fx, seller, Sell(item.Id, count: (byte)listed)));

        Assert.Equal(AuctionPackets.ResultSellInternalError, o.Result);
        Assert.Equal(1000, seller.Gold);
        Assert.Empty(fx.Auction.Articles);
        var row = Assert.Single(fx.Inventory.Rows);
        Assert.Equal((item.Id, item.Slot, stack), (row.Id, row.Slot, row.Count));
    }

    // ---------------------------------------------------------------- E-H11b: the 1406 answer

    private static byte[] Req1406(ulong articleUid, ulong itemUid, uint itemId, ulong price)
    {
        var blob = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), AuctionPackets.MsgTagReqBuySellingPrice);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8), articleUid);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(16), itemUid);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(24), itemId);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(28), price);
        return [1, 36, .. blob];
    }

    private sealed class ProbeBuy(PlayerStateManager pm)
        : AuctionBuySellingPriceHandler(null!, NullLogger<AuctionBuySellingPriceHandler>.Instance, pm,
            new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p, IServiceProvider sp)
            => HandleAsync(s, body, p, sp, CancellationToken.None);
    }

    /// <summary>The 2461 a 1406 draws, with the buyer's wallet and the auction untouched.</summary>
    private static async Task<(ushort Op, byte[] Body)> BuyOnceAsync(AuctionArticle article, long buyerGold)
    {
        await using var net = await Harness.StartAsync(1);
        var fx = new EconomyFixture();
        fx.AddPlayer(SellerId, gold: 0);
        var me = fx.AddPlayer(BuyerId, gold: buyerGold);
        me.SessionId = net.Sessions[0].SessionId;
        me.EntityId = 0x06EBC711;
        var a = fx.Auction.Seed(article);
        var (_, sp) = Build(fx);
        var pm = new PlayerStateManager(new NoDirtySink());
        var before = fx.Snapshot();

        await new ProbeBuy(pm).RunAsync(net.Sessions[0], Req1406(a.Uid, a.ItemUid, a.ItemId, (ulong)a.BuyoutPrice), me, sp);
        var frame = await net.Readers[0].NextAsync();

        // Nothing moved, nothing else was sent, and the wallet field of the answer is the unchanged one.
        EconomyFixture.CheckAll(before, fx.Snapshot());
        Assert.Equal(buyerGold, me.Gold);
        Assert.Equal(0, net.Clients[0].Available);
        Assert.Equal(a.State, fx.Auction.Articles.Single().State);
        Assert.Equal(
            Convert.ToHexString(AuctionPackets.BuildBuySellingPrice2461(me.EntityId, ResultOf(frame.Body), a.Uid, a.ItemUid, buyerGold)),
            Convert.ToHexString(frame.Body));
        return frame;
    }

    private static ushort ResultOf(byte[] body2461) => BinaryPrimitives.ReadUInt16LittleEndian(body2461.AsSpan(2 + 8)); // prefix 01 22, blob+8

    private static AuctionArticle Article(long buyout = 5000, uint seller = SellerId, AuctionArticleState state = AuctionArticleState.Active)
        => new()
        {
            Uid = 104_000_000_000_000_001UL, SellerCharId = seller, SellerName = "Seller", ItemId = TemplateId, ItemUid = 777,
            Count = 1, StartPrice = 100, BuyoutPrice = buyout, StartedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(24),
            State = state,
        };

    [Fact]
    public async Task Buyout_WithTooLittleZeny_IsAnswered2461With16302_AndTheWindowIsNotLeftPending()
    {
        var (op, body) = await BuyOnceAsync(Article(5000), buyerGold: 10);
        Assert.Equal(ZoneS2COpcodes.OnAuctionBuySellingPrice, op);
        Assert.Equal(AuctionPackets.ResultNotEnoughMoney, ResultOf(body));
    }

    [Fact]
    public async Task Buyout_OfAnArticleThatIsGone_IsAnswered2461With16308()
    {
        var (op, body) = await BuyOnceAsync(Article(5000, state: AuctionArticleState.Sold), buyerGold: 10_000);
        Assert.Equal(ZoneS2COpcodes.OnAuctionBuySellingPrice, op);
        Assert.Equal(AuctionPackets.ResultArticleGone, ResultOf(body));
    }

    [Fact]
    public async Task Buyout_OfOneOwnArticle_IsAnswered2461With16308()
    {
        var (_, body) = await BuyOnceAsync(Article(5000, seller: BuyerId), buyerGold: 10_000);
        Assert.Equal(AuctionPackets.ResultArticleGone, ResultOf(body));
    }

    [Fact]
    public async Task Buyout_OfAnArticleWithNoInstantPrice_IsAnswered2461With16308()
    {
        var (_, body) = await BuyOnceAsync(Article(buyout: 0), buyerGold: 10_000);
        Assert.Equal(AuctionPackets.ResultArticleGone, ResultOf(body));
    }

    // ---------------------------------------------------------------- E-H8: 1399 NPC gate

    private static byte[] Req1399(uint npcHandle, ushort tag = AuctionOpenHandler.MsgTagReqOpen)
    {
        var blob = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 0x06EBC711);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), npcHandle);
        return [1, 12, .. blob];
    }

    private sealed class ProbeOpen(PlayerStateManager pm)
        : AuctionOpenHandler(null!, NullLogger<AuctionOpenHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p, IServiceProvider sp)
            => HandleAsync(s, body, p, sp, CancellationToken.None);
    }

    private const uint NpcMap = 1100;

    private static (NpcStateManager Npcs, uint Handle) OneNpc()
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var spawn = new SpawnEntry { MapId = NpcMap, Index = 7, PosX = 0f, PosY = 0f, PosZ = 0f, AssetIndex = 30000001 };
        npcs.TrackSpawns(NpcMap, [spawn]);
        return (npcs, npcs.AllocateHandle(NpcMap, 7));
    }

    private static async Task<ushort> OpenOnceAsync(Func<uint, byte[]> request, Action<PlayerState> shape)
    {
        await using var net = await Harness.StartAsync(1);
        var (npcs, handle) = OneNpc();
        var sp = new ServiceCollection().AddSingleton(npcs).BuildServiceProvider();
        var pm = new PlayerStateManager(new NoDirtySink());
        var me = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x06EBC711, CharacterId = SellerId,
            MapId = NpcMap, X = 3f, Y = 4f };
        shape(me);

        await new ProbeOpen(pm).RunAsync(net.Sessions[0], request(handle), me, sp);
        var (op, body) = await net.Readers[0].NextAsync();

        Assert.Equal(ZoneS2COpcodes.OnAuctionOpen, op);
        Assert.Equal(0, net.Clients[0].Available);
        // 01 0a | 0a 00 | 5f e3 | u32 auction id | u16 result (blob+8)
        Assert.Equal(12, body.Length);
        Assert.Equal((ushort)0xE35F, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        return BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10));
    }

    [Fact]
    public async Task Open_AtALiveNearbyAuctionNpc_Opens() =>
        Assert.Equal((ushort)0, await OpenOnceAsync(h => Req1399(h), _ => { }));

    [Fact]
    public async Task Open_AtAHandleThatIsNoNpc_IsRefusedAndAnswered() =>
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(_ => Req1399(0x06EBC811), _ => { }));

    [Fact]
    public async Task Open_AtAnNpcHandleThatNothingTracks_IsRefusedAndAnswered() =>
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(_ => Req1399(0x000F4213), _ => { }));

    [Fact]
    public async Task Open_FromAnotherMap_Far_OrDead_IsRefused()
    {
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(h => Req1399(h), p => p.MapId = NpcMap + 1));
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused,
            await OpenOnceAsync(h => Req1399(h), p => p.X = ActorGate.NpcInteractReach + 1));
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(h => Req1399(h), p => p.IsDead = true));
    }

    [Fact]
    public async Task Open_WithAMalformedRequest_IsRefusedAndAnswered()
    {
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(h => Req1399(h, tag: 0x1234), _ => { }));
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(_ => [1, 4, 4, 0, 0x5E, 0xE3], _ => { }));
        Assert.Equal(AuctionOpenHandler.ResultOpenRefused, await OpenOnceAsync(_ => [], _ => { }));
    }
}
