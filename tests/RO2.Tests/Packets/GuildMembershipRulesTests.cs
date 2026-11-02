using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure unit tests of the Guild.Domain membership transitions, with no host service involved.</summary>
public class GuildMembershipRulesTests
{
    private static Guild NewGuild()
    {
        var g = new Guild { MasterCharId = 1, Ranks = { new() { Index = 0 }, new() { Index = 1 }, new() { Index = 4 } } };
        g.Members.Add(new GuildMember { CharId = 1, Gid = 101, AuthId = 0 });
        g.Members.Add(new GuildMember { CharId = 2, Gid = 102, AuthId = 1 });
        return g;
    }

    [Fact]
    public void Join_AddsOnLowestRank_StampsTime_AndClampsLevel_ThenIsIdempotent()
    {
        var g = NewGuild();
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var m = GuildMembershipRules.Join(g, 9, 109, "Nine", 0, 7, now);
        Assert.Equal((9u, 109ul, "Nine", GuildMember.MinLevel, (byte)7, (byte)4, (DateTime?)now),
            (m.CharId, m.Gid, m.Name, m.Level, m.Job, m.AuthId, m.JoinedUtc));
        Assert.Equal(3, g.Members.Count);
        Assert.Same(m, GuildMembershipRules.Join(g, 9, 999, "Other", 50, 1, now.AddDays(1)));
        Assert.Equal(3, g.Members.Count);
        Assert.Equal("Nine", m.Name);
    }

    [Fact]
    public void Leave_RemovesAndReturns_OrNullForNonMember()
    {
        var g = NewGuild();
        Assert.Equal(2u, GuildMembershipRules.Leave(g, 2)!.CharId);
        Assert.Single(g.Members);
        Assert.Null(GuildMembershipRules.Leave(g, 2));
    }

    [Fact]
    public void TrySetAuth_RefusesMasterRankZeroAndUnknownRank_ElseSetsAuth()
    {
        var g = NewGuild();
        var m = GuildMembershipRules.FindByGid(g, 102)!;
        Assert.Null(GuildMembershipRules.FindByGid(g, 555));
        Assert.False(GuildMembershipRules.TrySetAuth(g, GuildMembershipRules.FindByGid(g, 101)!, 4));
        Assert.False(GuildMembershipRules.TrySetAuth(g, m, 0));
        Assert.False(GuildMembershipRules.TrySetAuth(g, m, 3));
        Assert.Equal((byte)1, m.AuthId);
        Assert.True(GuildMembershipRules.TrySetAuth(g, m, 4));
        Assert.Equal((byte)4, m.AuthId);
    }

    [Fact]
    public void SetMaster_PromotesNewAndDropsOldToLowest()
    {
        var g = NewGuild();
        var m = GuildMembershipRules.FindByGid(g, 102)!;
        GuildMembershipRules.SetMaster(g, m);
        Assert.Equal(2u, g.MasterCharId);
        Assert.Equal((byte)0, m.AuthId);
        Assert.Equal((byte)4, GuildMembershipRules.FindByGid(g, 101)!.AuthId);

        GuildMembershipRules.SetMaster(g, m); // already master: stays rank 0
        Assert.Equal((byte)0, m.AuthId);
    }

    [Fact]
    public void SetMaster_WithOldMasterGone_OnlyPromotes()
    {
        var g = NewGuild();
        GuildMembershipRules.Leave(g, 1);
        var m = g.Members[0];
        GuildMembershipRules.SetMaster(g, m);
        Assert.Equal((2u, (byte)0), (g.MasterCharId, m.AuthId));
    }
}
