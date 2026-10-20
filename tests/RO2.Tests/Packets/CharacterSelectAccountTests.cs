using RO2.Domain.Entities;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Character-select account messages sent by ZoneAuthHandler: the 2820 OnPreOccupyCharacterNameACK answer to a
/// full 2307, the 2305 that drops a connection on duplicate login, the liveness check of the online-account
/// registry, and how a registry entry follows the account across a 1420 channel move.
/// </summary>
public class CharacterSelectAccountTests
{
    private static HashSet<uint> Set(params uint[] ids) => new(ids);

    [Fact]
    public void PreOccupy_FirstListOfAConnection_IsSentUpToSixCharacters()
    {
        Assert.True(ZoneAuthHandler.ShouldSendPreOccupyAck(null, Set()));                 
        Assert.True(ZoneAuthHandler.ShouldSendPreOccupyAck(null, Set(1, 2, 3, 4, 5, 6))); 
        Assert.False(ZoneAuthHandler.ShouldSendPreOccupyAck(null, Set(1, 2, 3, 4, 5, 6, 7)));
    }

    [Fact]
    public void PreOccupy_UnchangedReList_IsSent()
    {
        Assert.True(ZoneAuthHandler.ShouldSendPreOccupyAck(Set(4, 9), Set(9, 4)));
    }

    [Fact]
    public void PreOccupy_ReListAfterACreateOrADelete_IsNotSent()
    {
        Assert.False(ZoneAuthHandler.ShouldSendPreOccupyAck(Set(4, 9), Set(4, 9, 12))); 
        Assert.False(ZoneAuthHandler.ShouldSendPreOccupyAck(Set(4, 9), Set(9)));        
        Assert.False(ZoneAuthHandler.ShouldSendPreOccupyAck(Set(4), Set()));            
    }

    [Fact]
    public void PreOccupy_ListAfterTheAccountsFirstCharacter_IsSent()
    {
        Assert.True(ZoneAuthHandler.ShouldSendPreOccupyAck(Set(), Set(31)));
    }

    [Fact]
    public void DuplicateLogin2305_ReproducesTheRetailFrame_AndLeavesTheLogoutBodyAlone()
    {
        
        Assert.Equal(Convert.FromHexString("010a0a00f5d200000000ce3b"), ZoneAuthHandler.BuildDuplicateLoginNotify2305());
        Assert.Equal(Convert.FromHexString("010a0a00f5d2000000000000"), SessionExitPackets.DisconnectNotify2305);
    }

    [Fact]
    public void OnlineAccount_IsLiveOnlyWhileItsHeartbeatIsFresh()
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var entry = new OnlineAccountEntry { AccountId = 7, Owner = "x:1", SeenAt = now.AddSeconds(-2) };
        Assert.True(OnlineAccountRules.IsLive(entry, now));

        entry.SeenAt = now - OnlineAccountRules.StaleAfter;
        Assert.False(OnlineAccountRules.IsLive(entry, now));
    }

    private static PlayerState Pc(uint session, uint account) => new() { SessionId = session, AccountId = account };

    [Fact]
    public void OnlineAccount_ChannelMove_FollowsTheAccountToItsNewConnection()
    {
        
        
        var players = new[] { Pc(3, 1), Pc(5, 2), Pc(8, 1) };
        Assert.Equal(8u, ZoneAuthHandler.FindSuccessorSessionId(players, 1, 3, _ => true));
    }

    [Fact]
    public void OnlineAccount_ChannelMove_IgnoresClosedConnectionsAndOtherAccounts()
    {
        var players = new[] { Pc(3, 1), Pc(5, 2), Pc(8, 1) };
        Assert.Null(ZoneAuthHandler.FindSuccessorSessionId(players, 1, 3, id => id != 8)); 
        Assert.Null(ZoneAuthHandler.FindSuccessorSessionId(players, 7, 3, _ => true));      
        Assert.Null(ZoneAuthHandler.FindSuccessorSessionId(new[] { Pc(3, 1) }, 1, 3, _ => true));
    }

    [Fact]
    public void OnlineAccount_ChannelMove_PicksTheNewestConnection()
    {
        var players = new[] { Pc(9, 1), Pc(4, 1), Pc(12, 1) };
        Assert.Equal(12u, ZoneAuthHandler.FindSuccessorSessionId(players, 1, 3, _ => true));
    }

    [Fact]
    public void OnlineAccount_ClosedConnection_WaitsForAMoveOnlyWithinItsBounds()
    {
        var closed = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var settle = ZoneAuthHandler.SuccessorSettle;
        var cap = ChannelMoveTicketStore.Lifetime + settle;

        
        Assert.False(ZoneAuthHandler.SuccessorWaitOver(closed, null, closed + TimeSpan.FromSeconds(10)));
        Assert.True(ZoneAuthHandler.SuccessorWaitOver(closed, null, closed + cap));

        
        var t = closed + TimeSpan.FromSeconds(1);
        Assert.False(ZoneAuthHandler.SuccessorWaitOver(closed, t, t + settle - TimeSpan.FromMilliseconds(1)));
        Assert.True(ZoneAuthHandler.SuccessorWaitOver(closed, t, t + settle));
    }
}
