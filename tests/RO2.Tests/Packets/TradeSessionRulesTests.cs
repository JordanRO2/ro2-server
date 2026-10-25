using RO2.Economy.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The trade-window rules the reference server follows, checked on the server-side model the handlers drive.
/// </summary>
public class TradeSessionRulesTests
{
    private const uint A = 1, B = 2, C = 3;
    private const uint HA = (A << 8) | 0x11, HB = (B << 8) | 0x11, HC = (C << 8) | 0x11;

    private static bool AllOnline(uint _) => true;

    private static TradeSession OpenTrade(TradeSessionService svc)
    {
        var request = svc.Request(A, B, HA, HB, AllOnline);
        Assert.True(svc.TryAccept(request));
        return request;
    }

    [Fact]
    public void PartnerOfferChangeKeepsTheOtherSidesLock()
    {
        
        var trade = OpenTrade(new TradeSessionService());
        Assert.True(trade.SetLocked(A, true).Applied);

        Assert.Equal(TradeOfferChange.Applied, trade.AddItem(B, 0xAAul, 1));
        Assert.Equal(TradeOfferChange.Applied, trade.SetGold(B, 500));
        Assert.True(trade.RemoveItem(B, 0xAAul));

        var lockB = trade.SetLocked(B, true);
        Assert.True(lockB.Applied);
        Assert.True(lockB.BothLocked);
    }

    [Fact]
    public void LockedSideCannotChangeItsOfferOrLockAgain()
    {
        
        var trade = OpenTrade(new TradeSessionService());
        Assert.True(trade.SetLocked(A, true).Applied);

        Assert.Equal(TradeOfferChange.SideLocked, trade.AddItem(A, 0xAAul, 1));
        Assert.Equal(TradeOfferChange.SideLocked, trade.SetGold(A, 10));
        Assert.False(trade.SetLocked(A, true).Applied);
        Assert.Empty(trade.SnapshotItems(A));
        Assert.Equal(0, trade.OfferA.Gold);

        
        var unlock = trade.SetLocked(A, false);
        Assert.True(unlock.Applied);
        Assert.False(unlock.BothLocked);
    }

    [Fact]
    public void ReOfferAndEighthItemAreRefusedWithoutChangingTheOfferedQuantity()
    {
        var trade = OpenTrade(new TradeSessionService());
        Assert.Equal(TradeOfferChange.Applied, trade.AddItem(A, 100ul, 5));
        Assert.Equal(TradeOfferChange.AlreadyOffered, trade.AddItem(A, 100ul, 9));
        Assert.Equal((int?)5, TradeOfferQuantities.Get(trade, 100ul));

        for (ulong uid = 101; uid < 100 + (ulong)TradeOffer.MaxItems; uid++)
            Assert.Equal(TradeOfferChange.Applied, trade.AddItem(A, uid, 1));
        Assert.Equal(TradeOffer.MaxItems, trade.SnapshotItems(A).Length);
        Assert.Equal(TradeOfferChange.WindowFull, trade.AddItem(A, 999ul, 1));
        Assert.Equal(TradeOffer.MaxItems, trade.SnapshotItems(A).Length);
    }

    [Fact]
    public void ConfirmCountsOnlyWithBothSidesLockedAndCompletesOnce()
    {
        var trade = OpenTrade(new TradeSessionService());
        Assert.False(trade.ConfirmAndTryComplete(A)); 
        trade.SetLocked(A, true);
        trade.SetLocked(B, true);
        Assert.False(trade.ConfirmAndTryComplete(B));
        Assert.True(trade.ConfirmAndTryComplete(A));
        Assert.True(trade.Completing);
        Assert.False(trade.ConfirmAndTryComplete(A));
        Assert.Equal(TradeOfferChange.Closed, trade.AddItem(A, 1ul, 1));
    }

    [Fact]
    public void ReferenceRelockAfterConfirmIsIgnoredAndTheTradeStillCompletes()
    {
        
        
        var trade = OpenTrade(new TradeSessionService());
        Assert.False(trade.SetLocked(A, true).BothLocked);
        Assert.True(trade.SetLocked(B, true).BothLocked);
        Assert.False(trade.ConfirmAndTryComplete(B));
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(TradeOfferChange.SideLocked, trade.SetGold(B, 0));
            Assert.False(trade.SetLocked(B, true).Applied);
        }
        Assert.True(trade.ConfirmAndTryComplete(A));
    }

    [Fact]
    public void UnlockClearsBothConfirms()
    {
        var trade = OpenTrade(new TradeSessionService());
        trade.SetLocked(A, true);
        trade.SetLocked(B, true);
        Assert.False(trade.ConfirmAndTryComplete(A));
        trade.SetLocked(B, false);
        trade.SetLocked(B, true);
        Assert.False(trade.ConfirmAndTryComplete(B)); 
        Assert.True(trade.ConfirmAndTryComplete(A));
    }

    [Fact]
    public void NewRequestReplacesTheInitiatorsOldOneAndStaleAcceptFails()
    {
        var svc = new TradeSessionService();
        var toB = svc.Request(A, B, HA, HB, AllOnline);
        var toC = svc.Request(A, C, HA, HC, AllOnline);
        Assert.Same(toC, svc.RequestFrom(A));
        Assert.False(svc.TryAccept(toB));
        Assert.Null(svc.Get(B));
        Assert.True(svc.TryAccept(toC));
        Assert.Same(toC, svc.Get(A));
        Assert.Same(toC, svc.Get(C));
        Assert.True(toC.Accepted);
        Assert.Null(svc.RequestFrom(A));
    }

    [Fact]
    public void AcceptFailsWhileEitherSideIsTrading()
    {
        var svc = new TradeSessionService();
        var bToC = svc.Request(B, C, HB, HC, AllOnline);
        var aToB = svc.Request(A, B, HA, HB, AllOnline);
        Assert.True(svc.TryAccept(bToC));
        Assert.False(svc.TryAccept(aToB));
        Assert.Null(svc.Get(A));
    }

    [Fact]
    public void LiveTradeWithAnOfflinePartnerIsDropped()
    {
        var svc = new TradeSessionService();
        OpenTrade(svc);
        Assert.NotNull(svc.GetLive(A, AllOnline));
        Assert.Null(svc.GetLive(A, sid => sid != B));
        Assert.Null(svc.Get(A));
        Assert.Null(svc.Get(B));
    }

    [Fact]
    public void RequestsOfOfflinePlayersAreSwept()
    {
        var svc = new TradeSessionService();
        svc.Request(A, B, HA, HB, AllOnline);
        svc.Request(C, B, HC, HB, sid => sid != A);
        Assert.Null(svc.RequestFrom(A));
        Assert.NotNull(svc.RequestFrom(C));
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(RO2.Server.Zone.Models.PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    [Fact]
    public void ResolvePc_OnlyResolvesTheOnlinePlayerThatOwnsThePcHandle()
    {
        
        
        
        var players = new PlayerStateManager(new NoDirtySink());
        uint handle = PlayerStateManager.BuildActorHandle(5);
        players.AddPlayer(5, new RO2.Server.Zone.Models.PlayerState { SessionId = 5, EntityId = handle, Name = "Five" });

        Assert.Equal("Five", TradeSessionService.ResolvePc(players, handle)?.Name);
        Assert.Null(TradeSessionService.ResolvePc(players, (5u << 8) | 0x13));   
        Assert.Null(TradeSessionService.ResolvePc(players, PlayerStateManager.BuildActorHandle(6))); 

        
        players.AddPlayer(7, new RO2.Server.Zone.Models.PlayerState { SessionId = 7, EntityId = 0x0B01A011u, Name = "Seven" });
        Assert.Null(TradeSessionService.ResolvePc(players, PlayerStateManager.BuildActorHandle(7)));
    }

    [Fact]
    public void WithdrawOnlyRemovesTheCurrentRequest()
    {
        var svc = new TradeSessionService();
        var old = svc.Request(A, B, HA, HB, AllOnline);
        var current = svc.Request(A, B, HA, HB, AllOnline);
        Assert.False(svc.WithdrawRequest(old));
        Assert.True(svc.WithdrawRequest(current));
        Assert.Null(svc.RequestFrom(A));
    }
}
