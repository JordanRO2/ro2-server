using RO2.BuildingBlocks;
using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure tests of the guild funds deposit / withdraw sequencing: order of the steps and the undo on a failure.</summary>
public class GuildFundsUseCaseTests
{
    private sealed class FakeWallet(long gold, List<string> log) : IWallet
    {
        public long Gold { get; private set; } = gold;
        public long BankGold => 0;
        public bool TryDebit(long amount) { if (amount > Gold) return false; Gold -= amount; log.Add("debit"); return true; }
        public long DebitUpTo(long amount) => throw new NotSupportedException();
        public void Credit(long amount) { Gold += amount; log.Add("credit"); }
    }

    private sealed class FakeLedger(List<string> log) : IGuildFundsLedger
    {
        public FundsRefusal Refusal { get; set; } = FundsRefusal.None;
        public Task<(FundsRefusal, long)> AddAsync(Guild guild, long amount, long limit)
        { log.Add("add"); return Task.FromResult((Refusal, Refusal == FundsRefusal.None ? 500L : 0L)); }
        public Task<(FundsRefusal, long)> TakeAsync(Guild guild, long amount)
        { log.Add("take"); return Task.FromResult((Refusal, Refusal == FundsRefusal.None ? 500L : 0L)); }
        public Task<long> ReturnAsync(Guild guild, long amount) { log.Add("return"); return Task.FromResult(0L); }
    }

    private static (GuildFundsUseCase Uc, FakeLedger Ledger, FakeWallet Wallet, List<string> Log, Func<Task> Save, Guild G)
        Make(long wallet, bool saveFails = false, int failFromSave = 1)
    {
        var log = new List<string>();
        var ledger = new FakeLedger(log);
        var w = new FakeWallet(wallet, log);
        var n = 0;
        Task Save() { log.Add("save"); if (saveFails && ++n >= failFromSave) throw new IOException("db"); return Task.CompletedTask; }
        return (new GuildFundsUseCase(ledger), ledger, w, log, Save, new Guild());
    }

    [Fact]
    public async Task Deposit_DebitsAndWritesWalletBeforeAddingFunds()
    {
        var (uc, _, w, log, save, g) = Make(1_000);
        var m = await uc.DepositAsync(g, w, 300, 10_000, save);
        Assert.Equal(new FundsMove(FundsOutcome.Ok, 500), m);
        Assert.Equal(new[] { "debit", "save", "add" }, log);
        Assert.Equal(700, w.Gold);
    }

    [Fact]
    public async Task Deposit_WalletShort_TouchesNothing()
    {
        var (uc, _, w, log, save, g) = Make(100);
        var m = await uc.DepositAsync(g, w, 300, 10_000, save);
        Assert.Equal(FundsOutcome.WalletShort, m.Outcome);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Deposit_WalletWriteFails_CreditsBackAndNeverTouchesFunds()
    {
        var (uc, _, w, log, save, g) = Make(1_000, saveFails: true);
        var m = await uc.DepositAsync(g, w, 300, 10_000, save);
        Assert.Equal(FundsOutcome.WalletWriteFailed, m.Outcome);
        Assert.NotNull(m.Error);
        Assert.Equal(new[] { "debit", "save", "credit" }, log);
        Assert.Equal(1_000, w.Gold);
    }

    [Theory]
    [InlineData(FundsRefusal.OverLimit, FundsOutcome.OverLimit)]
    [InlineData(FundsRefusal.NotRegistered, FundsOutcome.NotRegistered)]
    [InlineData(FundsRefusal.WriteFailed, FundsOutcome.GuildWriteFailed)]
    public async Task Deposit_FundsRefused_RefundsAndWritesWallet(FundsRefusal refusal, FundsOutcome expected)
    {
        var (uc, ledger, w, log, save, g) = Make(1_000);
        ledger.Refusal = refusal;
        var m = await uc.DepositAsync(g, w, 300, 10_000, save);
        Assert.Equal(new FundsMove(expected, 0), m);
        Assert.Equal(new[] { "debit", "save", "add", "credit", "save" }, log);
        Assert.Equal(1_000, w.Gold);
    }

    [Fact]
    public async Task Deposit_RefundWriteFails_StillRefusesWithLiveWalletRestored()
    {
        var (uc, ledger, w, _, save, g) = Make(1_000, saveFails: true, failFromSave: 2);
        ledger.Refusal = FundsRefusal.OverLimit;
        var m = await uc.DepositAsync(g, w, 300, 10_000, save);
        Assert.Equal(FundsOutcome.OverLimit, m.Outcome);
        Assert.NotNull(m.RefundError);
        Assert.Equal(1_000, w.Gold);
    }

    [Fact]
    public async Task Withdraw_TakesFundsBeforeCreditingAndWritingWallet()
    {
        var (uc, _, w, log, save, g) = Make(0);
        var m = await uc.WithdrawAsync(g, w, 300, save);
        Assert.Equal(new FundsMove(FundsOutcome.Ok, 500), m);
        Assert.Equal(new[] { "take", "credit", "save" }, log);
        Assert.Equal(300, w.Gold);
    }

    [Theory]
    [InlineData(FundsRefusal.NotEnoughFunds, FundsOutcome.NotEnoughFunds)]
    [InlineData(FundsRefusal.NotRegistered, FundsOutcome.NotRegistered)]
    [InlineData(FundsRefusal.WriteFailed, FundsOutcome.GuildWriteFailed)]
    public async Task Withdraw_FundsRefused_NeverTouchesWallet(FundsRefusal refusal, FundsOutcome expected)
    {
        var (uc, ledger, w, log, save, g) = Make(0);
        ledger.Refusal = refusal;
        var m = await uc.WithdrawAsync(g, w, 300, save);
        Assert.Equal(new FundsMove(expected, 0), m);
        Assert.Equal(new[] { "take" }, log);
    }

    [Fact]
    public async Task Withdraw_WalletWriteFails_DebitsBackAndReturnsFunds()
    {
        var (uc, _, w, log, save, g) = Make(0, saveFails: true);
        var m = await uc.WithdrawAsync(g, w, 300, save);
        Assert.Equal(FundsOutcome.WalletWriteFailed, m.Outcome);
        Assert.NotNull(m.Error);
        Assert.Equal(new[] { "take", "credit", "save", "debit", "return" }, log);
        Assert.Equal(0, w.Gold);
    }

    [Fact]
    public async Task Withdraw_WalletWriteFailsAndZenySpent_KeepsTheWithdrawal()
    {
        var (uc, _, w, log, _, g) = Make(0);
        Task Save() { log.Add("save"); w.DebitUpTo0(); throw new IOException("db"); }
        var m = await uc.WithdrawAsync(g, w, 300, Save);
        Assert.Equal(FundsOutcome.OkWalletUnsaved, m.Outcome);
        Assert.Equal(500, m.FundsAfter);
        Assert.DoesNotContain("return", log);
    }
}

internal static class FakeWalletExt
{
    /// <summary>Spends the whole balance, as a concurrent purchase would between the credit and the failed write.</summary>
    public static void DebitUpTo0(this IWallet w) => w.TryDebit(w.Gold);
}
