using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>The pure decisions of the channel-move flow: the 1420 refusal rule and the 1421 ticket redemption.</summary>
public sealed class ChannelMoveUseCaseTests
{
    private static readonly uint[] Channels = [0x06514041, 0x06614081, 0x066140C1];

    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 5, 0, 0, TimeSpan.Zero);

    private static byte[] Token(byte seed) => Enumerable.Range(0, ChannelMoveTicketLedger.TokenBytes).Select(b => (byte)(seed + b)).ToArray();

    /// <summary>The port over the pure ledger, with a settable clock (the host store adds only locking).</summary>
    private sealed class LedgerStore : IChannelMoveTicketStore
    {
        private readonly ChannelMoveTicketLedger _ledger = new();
        public DateTimeOffset Now = T0;
        public int PendingCount => _ledger.PendingCount;
        public void Issue(ReadOnlySpan<byte> token, uint accountId, ulong characterUid, uint channelHandle)
            => _ledger.Issue(token, accountId, characterUid, channelHandle, Now);
        public ChannelMoveTicket? Consume(ReadOnlySpan<byte> token, uint accountId) => _ledger.Consume(token, accountId, Now);
        public int RevokeAccount(uint accountId) => _ledger.RemoveAccount(accountId);
    }

    [Fact]
    public void ResultCodes_AreTheRetailOnes()
    {
        Assert.Equal(1018, ChannelMoveRules.ResultCurrentChannel);
        Assert.Equal(1014, ChannelMoveRules.ResultNoSuchChannel);
    }

    [Fact]
    public void Decide_MoveToAnotherListedChannel_ProceedsToTheRequestedHandle()
    {
        var d = ChannelMoveUseCase.Decide(Channels[1], Channels[0], Channels);
        Assert.False(d.IsRefused);
        Assert.Null(d.RefusalCode);
        Assert.Equal(Channels[1], d.TargetChannel);
    }

    [Fact]
    public void Decide_MoveToCurrentChannel_Refuses1018()
    {
        var d = ChannelMoveUseCase.Decide(Channels[1], Channels[1], Channels);
        Assert.True(d.IsRefused);
        Assert.Equal(ChannelMoveRules.ResultCurrentChannel, d.RefusalCode);
    }

    [Fact]
    public void Decide_CurrentChannelIsCheckedBeforeTheChannelList()
    {
        // The current channel need not be listed: 1018 still wins over 1014.
        Assert.Equal(ChannelMoveRules.ResultCurrentChannel, ChannelMoveRules.RefusalCode(0x06510041, 0x06510041, Channels));
    }

    [Fact]
    public void Decide_UnreadableOrZeroCurrentChannel_SkipsTheCurrentCheck()
    {
        Assert.Null(ChannelMoveRules.RefusalCode(Channels[0], null, Channels));
        Assert.Equal(ChannelMoveRules.ResultNoSuchChannel, ChannelMoveRules.RefusalCode(0, 0, Channels));
        Assert.Equal(ChannelMoveRules.ResultNoSuchChannel, ChannelMoveRules.RefusalCode(0, null, Channels));
    }

    [Fact]
    public void Decide_UnlistedHandle_Refuses1014()
    {
        var d = ChannelMoveUseCase.Decide(0x06510041, Channels[0], Channels);
        Assert.Equal(ChannelMoveRules.ResultNoSuchChannel, d.RefusalCode);
        Assert.Equal(ChannelMoveRules.ResultNoSuchChannel, ChannelMoveRules.RefusalCode(1, null, []));
    }

    [Fact]
    public void Decide_StopsWalkingTheChannelsAtTheFirstMatch()
    {
        static IEnumerable<uint> Lazy()
        {
            yield return 5;
            throw new InvalidOperationException("walked past the match");
        }

        Assert.Null(ChannelMoveRules.RefusalCode(5, null, Lazy()));
    }

    [Fact]
    public void Redeem_IssuedTicket_IsAcceptedOnce()
    {
        var store = new LedgerStore();
        store.Issue(Token(1), 10, 99UL, Channels[1]);
        store.Now = T0 + TimeSpan.FromSeconds(30);

        var first = ChannelMoveUseCase.Redeem(store, Token(1), 10);
        Assert.True(first.IsAccepted);
        Assert.Equal(new ChannelMoveTicket(10, 99UL, Channels[1], T0), first.Ticket);
        Assert.False(ChannelMoveUseCase.Redeem(store, Token(1), 10).IsAccepted);
    }

    [Fact]
    public void Redeem_ExpiredTicket_IsRejected()
    {
        var store = new LedgerStore();
        store.Issue(Token(1), 10, 99UL, Channels[1]);
        store.Now = T0 + ChannelMoveTicketLedger.Lifetime + TimeSpan.FromTicks(1);

        Assert.False(ChannelMoveUseCase.Redeem(store, Token(1), 10).IsAccepted);
    }

    [Fact]
    public void Redeem_WrongAccountOrUnknownToken_IsRejectedAndKeepsTheTicket()
    {
        var store = new LedgerStore();
        store.Issue(Token(1), 10, 99UL, Channels[1]);

        Assert.False(ChannelMoveUseCase.Redeem(store, Token(1), 11).IsAccepted);
        Assert.False(ChannelMoveUseCase.Redeem(store, Token(2), 10).IsAccepted);
        Assert.True(ChannelMoveUseCase.Redeem(store, Token(1), 10).IsAccepted);
    }

    [Fact]
    public void Redeem_ANewerTicketOfTheAccountRevokesTheOlderOne()
    {
        var store = new LedgerStore();
        store.Issue(Token(1), 10, 99UL, Channels[0]);
        store.Issue(Token(2), 10, 99UL, Channels[2]);

        Assert.False(ChannelMoveUseCase.Redeem(store, Token(1), 10).IsAccepted);
        Assert.Equal(Channels[2], ChannelMoveUseCase.Redeem(store, Token(2), 10).Ticket!.ChannelHandle);
    }
}
