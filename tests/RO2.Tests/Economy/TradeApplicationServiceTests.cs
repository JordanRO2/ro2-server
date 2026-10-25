using RO2.BuildingBlocks;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// Pins the confirm-time swap of <see cref="TradeApplicationService"/> as the pre-extraction TradeConfirmHandler ran it,
/// over scripted ports that record every step in ONE ordered event list: both debits (or neither), the item exchange,
/// the cross-credit that follows only a successful exchange, the per-payer persists, and the refund of BOTH offers on
/// every failure (items that already moved stay moved: nothing here rolls them back).
/// </summary>
public sealed class TradeApplicationServiceTests
{
    private const uint CharA = 101;
    private const uint CharB = 202;
    private const uint ItemX = 5101;
    private const uint ItemY = 5102;
    private static readonly Guid Id = Guid.Parse("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f");

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
            events.Add($"debit:{tag}:{amount}");
            return true;
        }

        public long DebitUpTo(long amount) => throw new NotSupportedException();

        public void Credit(long amount)
        {
            Gold += amount;
            events.Add($"credit:{tag}:{amount}");
        }
    }

    /// <summary>The scripted exchange: it returns <see cref="Outcome"/> (or throws), having "moved" the items listed in
    /// <see cref="Moved"/>, which a faulted outcome leaves in place.</summary>
    private sealed class Inventory(List<string> events) : ITwoPartyTradeInventory
    {
        public TradeSwapOutcome Outcome { get; set; } = Swapped;
        public Exception? Throws { get; set; }
        public List<ulong> Moved { get; } = [];
        public TradeSwapRequest? Request { get; private set; }

        public Task<TradeSwapOutcome> SwapItemsAsync(TradeSwapRequest request)
        {
            Request = request;
            events.Add("swap");
            if (Throws is not null) throw Throws;
            if (Outcome.Status is TradeSwapStatus.Swapped or TradeSwapStatus.FaultedMidSwap)
                Moved.Add(request.A.Items[0].Uid);   // the first move landed; a mid-swap fault leaves it there
            return Task.FromResult(Outcome);
        }
    }

    private sealed class Log(List<string> events) : ITradeLog
    {
        public void OwnershipOrAffordabilityFailed(string nameA, string nameB) => events.Add("log:ownership");
        public void NoBagRoom(string nameA, string nameB, bool noRoomForA, bool noRoomForB) => events.Add($"log:noroom:{noRoomForA}:{noRoomForB}");
        public void CannotPay(string nameA, string nameB, string payer, long gold) => events.Add($"log:cannotpay:{payer}:{gold}");
        public void AbortedBeforeAnyMove(Exception ex, Guid tradeId, string nameA, string nameB) => events.Add($"log:before:{tradeId == Id}");
        public void FailedMidSwap(Exception ex, Guid tradeId, string nameA, string nameB, int writes, IReadOnlyList<ulong> notYetMoved, long goldA, long goldB)
            => events.Add($"log:mid:{writes}:{string.Join(",", notYetMoved)}:{goldA}:{goldB}");
        public void WalletNotPersisted(Exception ex, Guid tradeId, string name, long gold) => events.Add($"log:persistfail:{name}:{gold}");
        public void Completed(string nameA, int itemsA, long goldA, string nameB, int itemsB, long goldB)
            => events.Add($"log:done:{itemsA}:{goldA}:{itemsB}:{goldB}");
    }

    private static readonly TradeSwapOutcome Swapped = new(TradeSwapStatus.Swapped)
    {
        TemplatesFromA = [ItemX],
        TemplatesFromB = [ItemY, ItemX],
    };

    private static TradeSwapCommand Command(long goldA, long goldB) => new(Id, "Alice", "Bob", goldA, goldB,
        new TradeSwapSide(CharA, [new TradeOfferedItem(0xA1, 1, null)]),
        new TradeSwapSide(CharB, [new TradeOfferedItem(0xB1, null, null), new TradeOfferedItem(0xB2, 3, null)]));

    private sealed class Rig
    {
        public List<string> Events { get; } = [];
        public Wallet A { get; }
        public Wallet B { get; }
        public Inventory Inventory { get; }
        public Rig(long a, long b)
        {
            A = new Wallet(Events, "A", a);
            B = new Wallet(Events, "B", b);
            Inventory = new Inventory(Events);
        }

        public Task<TradeSwapResult> RunAsync(long goldA, long goldB, Func<Task>? persistA = null, Func<Task>? persistB = null)
            => new TradeApplicationService().ExecuteSwapAsync(Command(goldA, goldB), A, B, Inventory, new Log(Events),
                persistA ?? (() => { Events.Add("persist:A"); return Task.CompletedTask; }),
                persistB ?? (() => { Events.Add("persist:B"); return Task.CompletedTask; }));
    }

    [Fact]
    public async Task Success_DebitsBoth_ThenSwaps_ThenCrossCredits_ThenPersistsEachPayer()
    {
        var rig = new Rig(1000, 500);

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:100", "credit:B:300", "persist:A", "persist:B", "log:done:1:300:2:100"},             rig.Events);
        Assert.Equal(TradeResultKind.Completed, r.Kind);
        Assert.Equal((800L, 700L), (rig.A.Gold, rig.B.Gold));
        Assert.Equal((800L, 700L), (r.GoldA, r.GoldB));
        Assert.Equal(new[] {ItemX, ItemY}, r.MovedTemplates);          // distinct, A's first
        Assert.Equal(new[] {ItemY, ItemX}, r.ReceivedByA);              // what B gave
        Assert.Equal(new[] {ItemX}, r.ReceivedByB);                     // what A gave
        Assert.Equal(Id, rig.Inventory.Request!.TradeId);
        Assert.Equal((CharA, CharB), (rig.Inventory.Request.A.CharacterId, rig.Inventory.Request.B.CharacterId));
    }

    [Fact]
    public async Task PayerWhoCannotCoverItsOffer_EndsTheTradeBeforeAnyDebitOrMove()
    {
        var rig = new Rig(100, 500);

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"log:ownership"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
        Assert.Equal((100L, 500L), (rig.A.Gold, rig.B.Gold));
    }

    [Fact]
    public async Task SecondDebitRefused_CreditsTheFirstBack_AndMovesNothing()
    {
        var rig = new Rig(1000, 500) { B = { RefuseDebit = true } };

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"debit:A:300", "credit:A:300", "log:cannotpay:Bob:100"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
    }

    [Fact]
    public async Task FirstDebitRefused_TakesNothingFromEither()
    {
        var rig = new Rig(1000, 500) { A = { RefuseDebit = true } };

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"log:cannotpay:Alice:300"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
    }

    [Fact]
    public async Task OwnershipFailure_RefundsBoth_NoCrossCredit_NoPersist()
    {
        var rig = new Rig(1000, 500);
        rig.Inventory.Outcome = new TradeSwapOutcome(TradeSwapStatus.OwnershipFailed);

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:300", "credit:B:100", "log:ownership"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
    }

    [Fact]
    public async Task NoRoom_RefundsBoth_AndNamesWhichBagIsFull()
    {
        var rig = new Rig(1000, 500);
        rig.Inventory.Outcome = new TradeSwapOutcome(TradeSwapStatus.NoRoom) { NoRoomForB = true };

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:300", "credit:B:100", "log:noroom:False:True"}, rig.Events);
        Assert.Equal(TradeResultKind.NoRoom, r.Kind);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
    }

    [Fact]
    public async Task FaultBeforeAnyMove_RefundsBoth()
    {
        var rig = new Rig(1000, 500);
        rig.Inventory.Outcome = new TradeSwapOutcome(TradeSwapStatus.FaultedBeforeAnyMove) { Fault = new InvalidOperationException("x") };

        var r = await rig.RunAsync(300, 100);

        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:300", "credit:B:100", "log:before:True"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
        Assert.Empty(rig.Inventory.Moved);
    }

    [Fact]
    public async Task FaultMidSwap_RefundsBoth_ItemsAlreadyMovedStayMoved_AndTheLogNamesWhatWasNotMoved()
    {
        var rig = new Rig(1000, 500);
        rig.Inventory.Outcome = new TradeSwapOutcome(TradeSwapStatus.FaultedMidSwap)
        {
            Fault = new InvalidOperationException("x"), Writes = 1, NotYetMoved = [0xB1, 0xB2],
        };

        var r = await rig.RunAsync(300, 100);

        // Both offers go back, nothing is cross-credited or persisted, and the item that moved is NOT put back.
        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:300", "credit:B:100", "log:mid:1:177,178:300:100"}, rig.Events);
        Assert.Equal(TradeResultKind.Cancelled, r.Kind);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
        Assert.Equal(new[] {0xA1UL}, rig.Inventory.Moved);
    }

    [Fact]
    public async Task ThrowFromValidationOrPlanning_RefundsBoth_AndRethrows()
    {
        var rig = new Rig(1000, 500);
        rig.Inventory.Throws = new InvalidOperationException("read failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync(300, 100));

        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:300", "credit:B:100"}, rig.Events);
        Assert.Equal((1000L, 500L), (rig.A.Gold, rig.B.Gold));
    }

    [Fact]
    public async Task WalletRowWriteFailure_IsLogged_TheOtherPayerIsStillPersisted_AndTheTradeCompletes()
    {
        var rig = new Rig(1000, 500);

        var r = await rig.RunAsync(300, 100,
            persistA: () => throw new InvalidOperationException("db down"),
            persistB: () => { rig.Events.Add("persist:B"); return Task.CompletedTask; });

        Assert.Equal(TradeResultKind.Completed, r.Kind);
        Assert.Equal(new[] {"debit:A:300", "debit:B:100", "swap", "credit:A:100", "credit:B:300", "log:persistfail:Alice:800", "persist:B",
            "log:done:1:300:2:100"}, rig.Events);
    }

    [Fact]
    public async Task ZeroGoldOffers_StillRunTheFullSequence()
    {
        var rig = new Rig(0, 0);

        var r = await rig.RunAsync(0, 0);

        Assert.Equal(TradeResultKind.Completed, r.Kind);
        Assert.Equal(new[] {"debit:A:0", "debit:B:0", "swap", "credit:A:0", "credit:B:0", "persist:A", "persist:B", "log:done:1:0:2:0"}, rig.Events);
    }
}
