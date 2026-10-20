using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Zeny wallet rules: a debit the wallet cannot cover is refused without changing the balance, and the balance
/// never becomes negative.
/// </summary>
public sealed class PlayerWalletTests
{
    [Fact]
    public void CreditThenDebitOfTheSameAmount_RestoresTheBalance()
    {
        var player = new PlayerState { Gold = 500 };
        player.Credit(1_000);
        Assert.Equal(1_500, player.Gold);
        Assert.True(player.TryDebit(1_000));
        Assert.Equal(500, player.Gold);
    }

    [Fact]
    public void DebitOfExactlyTheBalance_Succeeds_AndLeavesZero()
    {
        var player = new PlayerState { Gold = 250 };
        Assert.True(player.TryDebit(250));
        Assert.Equal(0, player.Gold);
    }

    [Fact]
    public void DebitOfMoreThanTheBalance_IsRefused_AndLeavesTheBalanceUnchanged()
    {
        var player = new PlayerState { Gold = 100 };
        Assert.False(player.TryDebit(101));
        Assert.Equal(100, player.Gold);
    }

    [Fact]
    public void DebitOfANegativeAmount_IsRefused_AndLeavesTheBalanceUnchanged()
    {
        var player = new PlayerState { Gold = 100 };
        Assert.False(player.TryDebit(-1));
        Assert.False(player.TryDebit(long.MinValue));
        Assert.Equal(100, player.Gold);
    }

    [Fact]
    public void DebitUpTo_TakesMinOfAmountAndBalance_AndNeverGoesNegative()
    {
        var player = new PlayerState { Gold = 100 };
        Assert.Equal(30, player.DebitUpTo(30));
        Assert.Equal(70, player.Gold);
        Assert.Equal(70, player.DebitUpTo(1_000));   
        Assert.Equal(0, player.Gold);
        Assert.Equal(0, player.DebitUpTo(5));
        Assert.Equal(0, player.DebitUpTo(-5));
        Assert.Equal(0, player.Gold);
    }

    [Fact]
    public void GoldSetter_RejectsANegativeBalance()
    {
        var player = new PlayerState { Gold = 7 };
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Gold = -1);
        Assert.Equal(7, player.Gold);
    }

    [Fact]
    public void CreditOfANegativeAmount_Throws_AndLeavesTheBalanceUnchanged()
    {
        var player = new PlayerState { Gold = 7 };
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Credit(-1));
        Assert.Equal(7, player.Gold);
    }

    [Fact]
    public void Credit_SaturatesAtLongMaxValue_InsteadOfWrappingNegative()
    {
        var player = new PlayerState { Gold = long.MaxValue - 5 };
        player.Credit(100);
        Assert.Equal(long.MaxValue, player.Gold);
    }

    [Fact]
    public void ConcurrentSpenders_CannotSpendMoreThanTheWalletHolds()
    {
        const int spenders = 64, each = 10;
        var player = new PlayerState { Gold = 100 };   
        int paid = 0;
        Parallel.For(0, spenders, _ => { if (player.TryDebit(each)) Interlocked.Increment(ref paid); });
        Assert.Equal(10, paid);
        Assert.Equal(0, player.Gold);
    }

    [Fact]
    public void ZenyDelta_NegativeBeyondTheBalance_IsNotForgiven()
    {
        var player = new PlayerState { Gold = 40 };
        ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, -100);
        Assert.Equal(40, player.Gold);   

        ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, -15);
        Assert.Equal(25, player.Gold);

        ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, 75);
        Assert.Equal(100, player.Gold);
    }
}
