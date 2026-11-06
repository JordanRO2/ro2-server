using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure unit tests of the Guild.Domain rank rules, with no host service involved.</summary>
public class GuildRankRulesTests
{
    private static GuildRank Rank(byte index, string name, byte perms = 0) => new() { Index = index, Name = name, Permissions = perms };

    [Fact]
    public void DefaultRanks_HaveMasterAndMemberTitles_AndConstantsAreFixed()
    {
        var r = GuildRankRules.DefaultRanks();
        Assert.Equal(2, r.Count);
        Assert.Equal((0, "Master", (byte)0x0F), ((int)r[0].Index, r[0].Name, r[0].Permissions));
        Assert.Equal((1, "Member", (byte)0x00), ((int)r[1].Index, r[1].Name, r[1].Permissions));
        Assert.Equal(2, GuildRankRules.MinRanks);
        Assert.Equal(10, GuildRankRules.MaxRanks);
        Assert.Equal((byte)0x01, GuildRankRules.PermissionInvite);
        Assert.Equal((byte)0x02, GuildRankRules.PermissionKick);
        Assert.Equal((byte)0x0F, GuildRankRules.PermissionsAll);
    }

    [Fact]
    public void HasPermission_MasterBypasses_MemberNeedsAllBits_NonMemberNone()
    {
        var g = new Guild { MasterCharId = 7, Ranks = { Rank(1, "m", 0x03), Rank(2, "n") } };
        g.Members.Add(new GuildMember { CharId = 2, AuthId = 1 });
        g.Members.Add(new GuildMember { CharId = 3, AuthId = 2 });
        g.Members.Add(new GuildMember { CharId = 4, AuthId = 9 });
        Assert.True(GuildRankRules.HasPermission(g, 7, 0xFF));
        Assert.True(GuildRankRules.HasPermission(g, 2, 0x03));
        Assert.False(GuildRankRules.HasPermission(g, 2, 0x07));
        Assert.True(GuildRankRules.HasPermission(g, 3, 0x00));
        Assert.False(GuildRankRules.HasPermission(g, 4, 0x00));
        Assert.False(GuildRankRules.HasPermission(g, 99, 0x00));
    }

    [Fact]
    public void RankTitleOf_AndLowestRankIndex()
    {
        var g = new Guild { Ranks = { Rank(0, "a"), Rank(5, "b"), Rank(1, "c") } };
        g.Members.Add(new GuildMember { CharId = 1, AuthId = 5 });
        g.Members.Add(new GuildMember { CharId = 2, AuthId = 8 });
        Assert.Equal("b", GuildRankRules.RankTitleOf(g, 1));
        Assert.Equal(string.Empty, GuildRankRules.RankTitleOf(g, 2));
        Assert.Equal(string.Empty, GuildRankRules.RankTitleOf(g, 99));
        Assert.Equal((byte)5, GuildRankRules.LowestRankIndex(g));
        Assert.Equal((byte)0, GuildRankRules.LowestRankIndex(new Guild()));
    }

    [Fact]
    public void NormalizeRanks_SeedsPadsAndRederives_ThenIsIdempotent()
    {
        var g = new Guild { MasterCharId = 1, Ranks = null! };
        g.Members.Add(new GuildMember { CharId = 1, AuthId = 1 });
        g.Members.Add(new GuildMember { CharId = 2, AuthId = 0 });
        g.Members.Add(new GuildMember { CharId = 3, AuthId = 7 });
        Assert.True(GuildRankRules.NormalizeRanks(g));
        Assert.Equal(new[] { 0, 1 }, g.Ranks.Select(r => (int)r.Index));
        Assert.Equal(new byte[] { 0, 1, 1 }, g.Members.Select(m => m.AuthId));
        Assert.False(GuildRankRules.NormalizeRanks(g));

        var noMaster = new Guild { Ranks = { Rank(3, "x"), Rank(4, "y") } };
        Assert.True(GuildRankRules.NormalizeRanks(noMaster));
        Assert.Equal(new[] { 0, 3, 4 }, noMaster.Ranks.Select(r => (int)r.Index));
        Assert.Equal("Master", noMaster.Ranks[0].Name);
    }

    [Fact]
    public void NormalizeRanks_RewritesLegacyStringIdTitles()
    {
        var g = new Guild { MasterCharId = 1, Ranks = { Rank(0, " 11832"), Rank(1, " 11833") } };
        Assert.True(GuildRankRules.NormalizeRanks(g));
        Assert.Equal(new[] { "Master", "Member" }, g.Ranks.Select(r => r.Name));
        Assert.False(GuildRankRules.NormalizeRanks(g));   // idempotent once rewritten
    }
}
