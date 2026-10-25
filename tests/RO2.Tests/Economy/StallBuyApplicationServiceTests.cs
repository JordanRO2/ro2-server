using RO2.BuildingBlocks;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// Pins the stall buy of <see cref="StallBuyApplicationService"/> as the pre-extraction PrivateShopBuyItemHandler ran it,
/// over scripted ports that record every step in ONE ordered event list: the live-listing read, the affordability
/// refusals before anything changes, the buyer debit then seller credit under the wallet gate, the item writes, the
/// listing removal, and the two persists. There is NO compensation: a failing item write or persist leaves the zeny where
/// it moved (the known gap R1), and no inventory gate is taken (R4); these tests pin that, they do not bless it.
/// </summary>
public sealed class StallBuyApplicationServiceTests
{
    private const uint Buyer = 20;
    private const uint Seller = 10;
    private const uint Potion = 11100004;
    private const uint Sword = 13100001;
    private static readonly object Gate = new();

    private sealed class Wallet(List<string> events, string tag, long gold) : IWallet
    {
        public long Gold { get; private set; } = gold;
        public long BankGold => 0;

        /// <summary>Reads a balance high enough for the up-front check, yet refuses the debit (it was spent meanwhile).</summary>
        public bool RefuseDebit { get; set; }

        public bool TryDebit(long amount)
        {
            if (RefuseDebit || amount < 0 || Gold < amount) return false;
            Gold -= amount;
            events.Add($"debit:{tag}:{amount}:gate={Monitor.IsEntered(Gate)}");
            return true;
        }

        public long DebitUpTo(long amount) => throw new NotSupportedException();

        public void Credit(long amount)
        {
            Gold = Gold > long.MaxValue - amount ? long.MaxValue : Gold + amount;
            events.Add($"credit:{tag}:{amount}:gate={Monitor.IsEntered(Gate)}");
        }
    }

    private sealed class Inventory(List<string> events) : IStallBuyInventory
    {
        public IReadOnlyList<StallLiveListing> Live { get; set; } = [];
        public StallBuyerBag Bag { get; set; } = new([], [0, 1, 2]);
        public Exception? ApplyThrows { get; set; }
        public TradePlacement? AppliedPlan { get; private set; }
        public IReadOnlyList<byte> Removed { get; private set; } = [];

        public Task<IReadOnlyList<StallLiveListing>> ResolveLiveAsync(uint sellerCharacterId, IReadOnlyList<StallListing> picked)
        {
            events.Add($"live:{sellerCharacterId}");
            return Task.FromResult(Live);
        }

        public Task<StallBuyerBag> ReadBuyerBagAsync(uint buyerCharacterId)
        {
            events.Add($"bag:{buyerCharacterId}");
            return Task.FromResult(Bag);
        }

        public Task ApplyAsync(uint buyerCharacterId, IReadOnlyList<StallLiveListing> sold, TradePlacement plan)
        {
            events.Add($"apply:{buyerCharacterId}");
            AppliedPlan = plan;
            if (ApplyThrows is not null) throw ApplyThrows;
            return Task.CompletedTask;
        }

        public void RemoveListings(IReadOnlyList<byte> indices)
        {
            events.Add("remove:" + string.Join(",", indices));
            Removed = indices;
        }
    }

    private static StallLiveListing Live(byte index, uint uid, uint itemId, int held, ushort listed, ulong price, int stackMax)
        => new(new StallListing(index, uid, listed, price), itemId, held, stackMax);

    private static StallBuyCommand Cmd(IEnumerable<StallLiveListing> listings)
        => new(Buyer, Seller, listings.Select(l => l.Listing).ToList());

    private sealed class Rig
    {
        public readonly List<string> Events = [];
        public readonly Wallet BuyerWallet;
        public readonly Wallet SellerWallet;
        public readonly Inventory Inv;
        public Exception? PersistBuyerThrows { get; set; }

        public Rig(long buyerGold, long sellerGold)
        {
            BuyerWallet = new Wallet(Events, "buyer", buyerGold);
            SellerWallet = new Wallet(Events, "seller", sellerGold);
            Inv = new Inventory(Events);
        }

        public Task<StallBuyResult> Buy(StallBuyCommand cmd) => new StallBuyApplicationService().BuyAsync(
            cmd, BuyerWallet, SellerWallet, Gate, Inv,
            gold => { Events.Add($"persist:buyer:{gold}"); return PersistBuyerThrows is null ? Task.CompletedTask : throw PersistBuyerThrows; },
            gold => { Events.Add($"persist:seller:{gold}"); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Success_RunsLiveBagDebitCreditApplyRemovePersistInOrder()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 50);
        rig.Inv.Live = [Live(3, 77, Potion, held: 5, listed: 2, price: 100, stackMax: 10)];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["live:10", "bag:20", "debit:buyer:100:gate=True", "credit:seller:100:gate=True", "apply:20", "remove:3",
             "persist:buyer:900", "persist:seller:150"],
            rig.Events);
        Assert.Equal((100UL, 900L, 150L), (result.Total, result.BuyerGoldAfter, result.SellerGoldAfter));
        Assert.Equal([Potion], result.SoldItemIds);
        Assert.Equal((900L, 150L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));
        // A partial listing of a stackable item with no held stack lands in the first free cell.
        var move = Assert.Single(rig.Inv.AppliedPlan!.Moves);
        Assert.Equal(((short)0, 2), (move.Cell, move.CellCount));
        Assert.Equal(2, move.Item.Qty);
    }

    [Fact]
    public async Task Success_MergesIntoAHeldStackBeforeFillingACell_AndReportsTheFillOrder()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 0);
        rig.Inv.Bag = new StallBuyerBag([new BagStack(900, Potion, 9, 0)], [5, 6]);
        rig.Inv.Live = [Live(0, 77, Potion, held: 5, listed: 2, price: 100, stackMax: 10)];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.True(result.Succeeded);
        var plan = rig.Inv.AppliedPlan!;
        Assert.Equal(10, plan.HeldCounts[900]);                       // 9 + 1 up to Stack_Max
        Assert.Equal(((short)5, 1), (plan.Moves[0].Cell, plan.Moves[0].CellCount));   // the other 1 takes a cell
        Assert.Equal([new TradeFill(900, -1), new TradeFill(null, 0)], plan.Filled);  // held first, then the new cell
    }

    [Fact]
    public async Task Success_TotalsEveryPickedListingAndRemovesThemInPickOrder()
    {
        var rig = new Rig(buyerGold: 500, sellerGold: 0);
        rig.Inv.Live =
        [
            Live(4, 71, Sword, held: 1, listed: 1, price: 200, stackMax: 1),
            Live(1, 72, Potion, held: 5, listed: 5, price: 250, stackMax: 10),
        ];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.True(result.Succeeded);
        Assert.Equal(450UL, result.Total);
        Assert.Equal([Sword, Potion], result.SoldItemIds);
        Assert.Equal<byte>([4, 1], rig.Inv.Removed);
        Assert.Equal((50L, 450L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));
    }

    [Fact]
    public async Task Refusal_ListingGoneFromTheSellersBag_ChangesNothing()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 0);
        var picked = new[] { Live(0, 77, Potion, 5, 2, 100, 10), Live(1, 78, Sword, 1, 1, 100, 1) };
        rig.Inv.Live = [picked[0]];   // the second listing's item left the bag

        var result = await rig.Buy(Cmd(picked));

        Assert.Equal(StallCodes.ResultAlreadySold, result.Refusal);
        Assert.Equal(["live:10"], rig.Events);
        Assert.Equal((1000L, 0L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));
    }

    [Fact]
    public async Task Refusal_TotalOverflowOrUnaffordable_IsNotEnoughMoneyBeforeTheBuyerBagIsRead()
    {
        var overflow = new Rig(buyerGold: 1000, sellerGold: 0);
        overflow.Inv.Live = [Live(0, 1, Sword, 1, 1, ulong.MaxValue, 1), Live(1, 2, Potion, 1, 1, 1, 10)];
        var r1 = await overflow.Buy(Cmd(overflow.Inv.Live));
        Assert.Equal((StallCodes.ResultNotEnoughMoney, "the total overflows"), (r1.Refusal, r1.Reason));
        Assert.Equal(["live:10"], overflow.Events);

        var tooBig = new Rig(buyerGold: long.MaxValue, sellerGold: 0);
        tooBig.Inv.Live = [Live(0, 1, Sword, 1, 1, (ulong)long.MaxValue + 1, 1)];   // fits a ulong, not a wallet
        Assert.Equal(StallCodes.ResultNotEnoughMoney, (await tooBig.Buy(Cmd(tooBig.Inv.Live))).Refusal);
        Assert.Equal(["live:10"], tooBig.Events);

        var poor = new Rig(buyerGold: 99, sellerGold: 0);
        poor.Inv.Live = [Live(0, 1, Sword, 1, 1, 100, 1)];
        var r3 = await poor.Buy(Cmd(poor.Inv.Live));
        Assert.Equal((StallCodes.ResultNotEnoughMoney, "99 zeny for 100"), (r3.Refusal, r3.Reason));
        Assert.Equal(["live:10"], poor.Events);
    }

    [Fact]
    public async Task Refusal_NoBagRoom_ChangesNothing()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 0);
        rig.Inv.Bag = new StallBuyerBag([], []);
        rig.Inv.Live = [Live(0, 1, Sword, 1, 1, 100, 1)];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.Equal((StallCodes.ResultNoBagRoom, "no free bag cell"), (result.Refusal, result.Reason));
        Assert.Equal(["live:10", "bag:20"], rig.Events);
        Assert.Equal((1000L, 0L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));
    }

    [Fact]
    public async Task Refusal_SellerWalletLimit_IsCheckedBeforeTheBuyerIsDebited()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: long.MaxValue - 99);
        rig.Inv.Live = [Live(0, 1, Sword, 1, 1, 100, 1)];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.Equal(StallCodes.ResultSellerMoneyLimit, result.Refusal);
        Assert.Equal(["live:10", "bag:20"], rig.Events);   // no debit, no credit, nothing written
        Assert.Equal(1000L, rig.BuyerWallet.Gold);
    }

    [Fact]
    public async Task Refusal_DebitRefusedAfterTheCheck_CreditsNobodyAndWritesNothing()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 0);
        rig.BuyerWallet.RefuseDebit = true;
        rig.Inv.Live = [Live(0, 1, Sword, 1, 1, 100, 1)];

        var result = await rig.Buy(Cmd(rig.Inv.Live));

        Assert.Equal(StallCodes.ResultNotEnoughMoney, result.Refusal);
        Assert.Equal(["live:10", "bag:20"], rig.Events);
        Assert.Equal(0L, rig.SellerWallet.Gold);
    }

    [Fact]
    public async Task ItemWriteFault_PropagatesWithTheZenyAlreadyMoved_NoRefundNoRemovalNoPersist()
    {
        // The current behaviour (known gap R1): nothing compensates the debit/credit when an item write throws.
        var rig = new Rig(buyerGold: 1000, sellerGold: 50);
        rig.Inv.Live = [Live(3, 77, Potion, 5, 2, 100, 10)];
        rig.Inv.ApplyThrows = new InvalidOperationException("write failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Buy(Cmd(rig.Inv.Live)));

        Assert.Equal("write failed", ex.Message);
        Assert.Equal(["live:10", "bag:20", "debit:buyer:100:gate=True", "credit:seller:100:gate=True", "apply:20"], rig.Events);
        Assert.Equal((900L, 150L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));   // not refunded
        Assert.Empty(rig.Inv.Removed);                                                 // the listing stays listed
    }

    [Fact]
    public async Task PersistFault_PropagatesAfterTheListingsWereRemoved_SellerIsNotPersisted()
    {
        var rig = new Rig(buyerGold: 1000, sellerGold: 50) { PersistBuyerThrows = new TimeoutException("db") };
        rig.Inv.Live = [Live(3, 77, Potion, 5, 2, 100, 10)];

        await Assert.ThrowsAsync<TimeoutException>(() => rig.Buy(Cmd(rig.Inv.Live)));

        Assert.Equal(
            ["live:10", "bag:20", "debit:buyer:100:gate=True", "credit:seller:100:gate=True", "apply:20", "remove:3", "persist:buyer:900"],
            rig.Events);
        Assert.Equal((900L, 150L), (rig.BuyerWallet.Gold, rig.SellerWallet.Gold));
    }

    [Theory]
    [InlineData(100UL, 100L, true)]
    [InlineData(101UL, 100L, false)]
    [InlineData(0UL, 0L, true)]
    [InlineData((ulong)long.MaxValue, long.MaxValue, true)]
    [InlineData((ulong)long.MaxValue + 1, long.MaxValue, false)]
    [InlineData(ulong.MaxValue, long.MaxValue, false)]
    public void StallPricing_CanAfford_RefusesATotalPastTheWalletRange(ulong total, long gold, bool expected)
        => Assert.Equal(expected, StallPricing.CanAfford(total, gold));

    [Fact]
    public void StallPricing_TryTotal_SumsWholeListingPricesAndStopsAtTheOverflow()
    {
        Assert.True(StallPricing.TryTotal([100UL, 250UL], out var total));
        Assert.Equal(350UL, total);
        Assert.True(StallPricing.TryTotal([ulong.MaxValue - 1, 1UL], out var edge));
        Assert.Equal(ulong.MaxValue, edge);
        Assert.False(StallPricing.TryTotal([ulong.MaxValue, 1UL], out _));
        Assert.True(StallPricing.TryTotal([], out var none));
        Assert.Equal(0UL, none);
    }

    [Fact]
    public void StallPricing_ExceedsSellerLimit_IsPastTheWalletMaximum()
    {
        Assert.False(StallPricing.ExceedsSellerLimit(long.MaxValue - 100, 100));
        Assert.True(StallPricing.ExceedsSellerLimit(long.MaxValue - 99, 100));
    }

    [Fact]
    public void StallBuyRules_Eligibility_ChecksOwnerThenOpenThenViewer()
    {
        Assert.Equal(StallCodes.ResultWrongBuyer, StallBuyRules.EligibilityRefusal(true, false, false));
        Assert.Equal(StallCodes.ResultShopClosed, StallBuyRules.EligibilityRefusal(false, false, true));
        Assert.Equal(StallCodes.ResultWrongBuyer, StallBuyRules.EligibilityRefusal(false, true, false));
        Assert.Equal(0, StallBuyRules.EligibilityRefusal(false, true, true));
        Assert.True(StallBuyRules.IsListingLive(true, 2, 2));
        Assert.False(StallBuyRules.IsListingLive(true, 1, 2));
        Assert.False(StallBuyRules.IsListingLive(false, 9, 2));
    }

    [Fact]
    public void StallCodes_KeepTheirWireValuesAndWidths()
    {
        Assert.Equal(
            [(ushort)23006, (ushort)23019, (ushort)23020, (ushort)23021, (ushort)23023, (ushort)23026],
            new[]
            {
                StallCodes.ResultAlreadySold, StallCodes.ResultWrongBuyer, StallCodes.ResultNoBagRoom,
                StallCodes.ResultSellerMoneyLimit, StallCodes.ResultShopClosed, StallCodes.ResultNotEnoughMoney,
            });
    }
}
