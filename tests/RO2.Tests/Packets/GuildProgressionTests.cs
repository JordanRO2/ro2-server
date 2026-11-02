using RO2.GameData.Tables;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure unit tests of the Guild.Domain progression rules, with no host service involved.</summary>
public class GuildProgressionTests
{
    private const ulong Cap = long.MaxValue;

    [Fact]
    public void CreditExp_AddsSaturatesAndPullsAboveCapDown()
    {
        var g = new Guild { Exp = 10 };
        Assert.Equal((10UL, 15UL), GuildProgression.CreditExp(g, 5));
        Assert.Equal((15UL, Cap), GuildProgression.CreditExp(g, ulong.MaxValue));
        Assert.Equal((Cap, Cap), GuildProgression.CreditExp(g, 0));
        g.Exp = ulong.MaxValue;
        Assert.Equal((ulong.MaxValue, Cap), GuildProgression.CreditExp(g, 0));
        Assert.Equal(Cap, g.Exp);
    }

    [Fact]
    public void CreditMemberExp_CreditsContribSaturating_AndGuildOnlyForANonMember()
    {
        var g = new Guild { Exp = 0 };
        g.Members.Add(new GuildMember { CharId = 1, ContribPoints = long.MaxValue - 3 });
        Assert.Equal((0UL, 10UL), GuildProgression.CreditMemberExp(g, 1, 10));
        Assert.Equal(long.MaxValue, g.Members[0].ContribPoints);
        Assert.Equal((10UL, 15UL), GuildProgression.CreditMemberExp(g, 99, 5));
        Assert.Equal(long.MaxValue, g.Members[0].ContribPoints);
    }

    [Fact]
    public void SetBribePercent_CapsAtFifty_ReportsChange_NullForNonMember()
    {
        var g = new Guild();
        g.Members.Add(new GuildMember { CharId = 1 });
        Assert.Equal((byte?)30, GuildProgression.SetBribePercent(g, 1, 30, out var changed));
        Assert.True(changed);
        Assert.Equal((byte?)30, GuildProgression.SetBribePercent(g, 1, 30, out changed));
        Assert.False(changed);
        Assert.Equal((byte?)GuildProgression.MaxBribePercent, GuildProgression.SetBribePercent(g, 1, 255, out changed));
        Assert.True(changed);
        Assert.Equal((byte)50, GuildProgression.BribePercentOf(g, 1));
        Assert.Null(GuildProgression.SetBribePercent(g, 2, 10, out changed));
        Assert.False(changed);
        Assert.Equal((byte)0, GuildProgression.BribePercentOf(g, 2));
    }

    [Fact]
    public void BribeAmounts_RoundDown()
    {
        Assert.Equal(1UL, GuildProgression.LevelUpBribeAmount(199));
        Assert.Equal(ulong.MaxValue / 100, GuildProgression.LevelUpBribeAmount(ulong.MaxValue));
        Assert.Equal(15UL, GuildProgression.HuntingBribeAmount(300, 5));
        Assert.Equal(0UL, GuildProgression.HuntingBribeAmount(19, 5));
    }

    [Fact]
    public void GuildLevelOf_DerivesLevelFromLifetimeExp()
    {
        var table = new[]
        {
            new GuildExpTable { Guild_Base_Level = 2, Guild_Base_Exp_Next = 200 },
            new GuildExpTable { Guild_Base_Level = 1, Guild_Base_Exp_Next = 100 },
            new GuildExpTable { Guild_Base_Level = 3, Guild_Base_Exp_Next = 0 },
        };
        Assert.Equal(((byte)0, 7UL), GuildProgression.GuildLevelOf(7, null));
        Assert.Equal(((byte)1, 99UL), GuildProgression.GuildLevelOf(99, table));
        Assert.Equal(((byte)2, 199UL), GuildProgression.GuildLevelOf(299, table));
        Assert.Equal(((byte)3, 5UL), GuildProgression.GuildLevelOf(305, table));
    }
}
