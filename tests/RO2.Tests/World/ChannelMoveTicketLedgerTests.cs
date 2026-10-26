using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Pure rules of <see cref="ChannelMoveTicketLedger"/>: lifetime, single use, one live ticket per account, lazy expiry.</summary>
public sealed class ChannelMoveTicketLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 21, 5, 50, 10, TimeSpan.Zero);

    private static byte[] Token(byte seed) => Enumerable.Range(0, ChannelMoveTicketLedger.TokenBytes).Select(b => (byte)(seed + b)).ToArray();

    [Fact]
    public void Lifetime_IsThirtySeconds_AndTokenIsSixteenBytes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ChannelMoveTicketLedger.Lifetime);
        Assert.Equal(16, ChannelMoveTicketLedger.TokenBytes);
    }

    [Fact]
    public void Consume_ReturnsTheIssuedTicket_Once()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 99UL, 0x06614081, T0);

        var ticket = ledger.Consume(Token(1), 10, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(new ChannelMoveTicket(10, 99UL, 0x06614081, T0), ticket);
        Assert.Null(ledger.Consume(Token(1), 10, T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void Consume_AtExactlyTheLifetimeSucceeds_JustAfterFails()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        Assert.NotNull(ledger.Consume(Token(1), 10, T0 + ChannelMoveTicketLedger.Lifetime));

        ledger.Issue(Token(2), 10, 1, 1, T0);
        Assert.Null(ledger.Consume(Token(2), 10, T0 + ChannelMoveTicketLedger.Lifetime + TimeSpan.FromTicks(1)));
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void Consume_ByTheWrongAccount_TakesAndRevokesNothing()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        ledger.Issue(Token(2), 11, 1, 1, T0);

        Assert.Null(ledger.Consume(Token(1), 11, T0));
        Assert.Equal(2, ledger.PendingCount);
        Assert.NotNull(ledger.Consume(Token(1), 10, T0));
    }

    [Fact]
    public void Issue_RevokesTheAccountsEarlierTicket_ButNotOtherAccounts()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        ledger.Issue(Token(2), 11, 1, 1, T0);
        ledger.Issue(Token(3), 10, 1, 1, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(2, ledger.PendingCount);
        Assert.Null(ledger.Consume(Token(1), 10, T0 + TimeSpan.FromSeconds(2)));
        Assert.NotNull(ledger.Consume(Token(3), 10, T0 + TimeSpan.FromSeconds(2)));
        Assert.NotNull(ledger.Consume(Token(2), 11, T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Issue_SweepsExpiredTicketsOfOtherAccounts()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 99, 1, 1, T0);
        ledger.Issue(Token(2), 10, 1, 1, T0 + TimeSpan.FromMinutes(1));

        Assert.Equal(1, ledger.PendingCount);
    }

    [Fact]
    public void SuccessfulConsume_LeavesOtherAccountsTicketsAlone()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        ledger.Issue(Token(2), 11, 1, 1, T0);

        Assert.NotNull(ledger.Consume(Token(1), 10, T0));
        Assert.Equal(1, ledger.PendingCount);
        Assert.NotNull(ledger.Consume(Token(2), 11, T0));
    }

    [Fact]
    public void RemoveAccount_CountsAndDropsOnlyThatAccount()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        ledger.Issue(Token(2), 11, 1, 1, T0);

        Assert.Equal(1, ledger.RemoveAccount(10));
        Assert.Equal(0, ledger.RemoveAccount(10));
        Assert.Null(ledger.Consume(Token(1), 10, T0));
        Assert.NotNull(ledger.Consume(Token(2), 11, T0));
    }

    [Fact]
    public void SweepExpired_DropsOnlyTicketsPastTheLifetime()
    {
        var ledger = new ChannelMoveTicketLedger();
        ledger.Issue(Token(1), 10, 1, 1, T0);
        ledger.Issue(Token(2), 11, 1, 1, T0 + TimeSpan.FromSeconds(20));

        ledger.SweepExpired(T0 + TimeSpan.FromSeconds(31));

        Assert.Equal(1, ledger.PendingCount);
        Assert.NotNull(ledger.Consume(Token(2), 11, T0 + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void WrongWidthToken_IssueThrows_ConsumeReturnsNull()
    {
        var ledger = new ChannelMoveTicketLedger();
        Assert.Throws<ArgumentException>(() => ledger.Issue(new byte[15], 1, 1, 1, T0));
        Assert.Null(ledger.Consume(new byte[17], 1, T0));
    }
}
