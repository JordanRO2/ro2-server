using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure unit tests of the Guild.Domain funds rules, with no host service involved.</summary>
public class GuildFundsRulesTests
{
    [Fact]
    public void TryAddFunds_RefusesNegative_AcceptsUpToLimit_AndGuardsOverflow()
    {
        var g = new Guild { Gold = 100 };
        Assert.False(GuildFundsRules.TryAddFunds(g, -1, 1_000, out var after));
        Assert.Equal(100, after);
        Assert.True(GuildFundsRules.TryAddFunds(g, 900, 1_000, out after));
        Assert.Equal(1_000, after);
        Assert.False(GuildFundsRules.TryAddFunds(g, 1, 1_000, out after));
        Assert.Equal(1_000, g.Gold);

        var big = new Guild { Gold = long.MaxValue - 1 };
        Assert.False(GuildFundsRules.TryAddFunds(big, 2, long.MaxValue, out after));
        Assert.True(GuildFundsRules.TryAddFunds(big, 1, long.MaxValue, out after));
        Assert.Equal(long.MaxValue, after);
    }

    [Fact]
    public void TryAddFunds_GoldAboveLimit_RefusesEvenZero()
    {
        var g = new Guild { Gold = 500 };
        Assert.False(GuildFundsRules.TryAddFunds(g, 0, 400, out var after));
        Assert.Equal(500, after);
    }

    [Fact]
    public void TryTakeFunds_RefusesNegativeAndMoreThanHeld()
    {
        var g = new Guild { Gold = 50 };
        Assert.False(GuildFundsRules.TryTakeFunds(g, -1, out var after));
        Assert.False(GuildFundsRules.TryTakeFunds(g, 51, out after));
        Assert.Equal(50, after);
        Assert.True(GuildFundsRules.TryTakeFunds(g, 50, out after));
        Assert.Equal(0, after);
    }

    [Fact]
    public void ReturnFunds_Adds_SaturatesAtLongMax_AndNegativeSaturatesToo()
    {
        var g = new Guild { Gold = 10 };
        Assert.Equal(15, GuildFundsRules.ReturnFunds(g, 5));
        g.Gold = long.MaxValue - 5;
        Assert.Equal(long.MaxValue, GuildFundsRules.ReturnFunds(g, 5));
        Assert.Equal(long.MaxValue, GuildFundsRules.ReturnFunds(g, long.MaxValue));
        // quirk: long.MaxValue - negative wraps, so the saturation branch fires
        Assert.Equal(long.MaxValue, GuildFundsRules.ReturnFunds(new Guild { Gold = 10 }, -1));
    }
}
