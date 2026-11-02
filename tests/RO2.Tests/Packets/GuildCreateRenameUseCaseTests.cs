using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure create / rename decisions (RO2.Guild.Application), no host.</summary>
public class GuildCreateRenameUseCaseTests
{
    [Theory]
    [InlineData(true, "", 0L, 100L, CreateCheck.AlreadyInGuild)]      // membership beats a blank name and a short wallet
    [InlineData(false, "  ", 0L, 100L, CreateCheck.NameBlank)]        // blank beats a short wallet
    [InlineData(false, "Name", 99L, 100L, CreateCheck.NotEnoughZeny)]
    [InlineData(false, "Name", 100L, 100L, CreateCheck.Proceed)]
    [InlineData(false, "Name", 0L, 0L, CreateCheck.Proceed)]
    public void CheckRequest_orders_membership_name_wallet(bool inGuild, string name, long gold, long fee, CreateCheck expected)
        => Assert.Equal(expected, GuildCreateUseCase.CheckRequest(inGuild, name, gold, fee));

    [Theory]
    [InlineData(true, true, CreateRegistration.AlreadyInGuild)]
    [InlineData(true, false, CreateRegistration.AlreadyInGuild)]
    [InlineData(false, true, CreateRegistration.NameTaken)]
    [InlineData(false, false, CreateRegistration.Register)]
    public void DecideRegistration_checks_membership_before_name(bool inGuild, bool taken, CreateRegistration expected)
        => Assert.Equal(expected, GuildCreateUseCase.DecideRegistration(inGuild, taken));

    [Theory]
    [InlineData(true, false, CreateSettlement.Created)]
    [InlineData(false, true, CreateSettlement.NameTakenRefund)]
    [InlineData(false, false, CreateSettlement.AlreadyInGuildRefund)]
    public void Settle_refunds_every_refusal(bool created, bool nameTaken, CreateSettlement expected)
        => Assert.Equal(expected, GuildCreateUseCase.Settle(created, nameTaken));

    [Fact]
    public void Found_makes_the_founder_the_only_member_on_rank_zero()
    {
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var guid = new byte[Guild.GuidLength];
        var g = GuildCreateUseCase.Found(7, guid, "Name", 42, 99UL, "Founder", 0, 3, at);

        Assert.Equal(7u, g.Id);
        Assert.Same(guid, g.Guid16);
        Assert.Equal("Name", g.Name);
        Assert.Equal(42u, g.MasterCharId);
        Assert.Equal(at, g.CreatedUtc);
        Assert.Equal(string.Empty, g.Notice);
        Assert.Equal(GuildRankRules.DefaultRanks().Count, g.Ranks.Count);
        var m = Assert.Single(g.Members);
        Assert.Equal((42u, 99UL, "Founder", GuildMember.MinLevel, (byte)3, GuildRankRules.MasterRankIndex, at),
            (m.CharId, m.Gid, m.Name, m.Level, m.Job, m.AuthId, m.JoinedUtc));
    }

    [Theory]
    [InlineData(false, true, RenameDecision.NotRegistered)]
    [InlineData(false, false, RenameDecision.NotRegistered)]
    [InlineData(true, true, RenameDecision.NameTaken)]
    [InlineData(true, false, RenameDecision.Rename)]
    public void Decide_checks_registration_before_name(bool registered, bool taken, RenameDecision expected)
        => Assert.Equal(expected, GuildRenameUseCase.Decide(registered, taken));

    [Fact]
    public void ShouldRestoreName_only_when_the_attempted_name_is_still_set()
    {
        Assert.True(GuildRenameUseCase.ShouldRestoreName("New", "New"));
        Assert.False(GuildRenameUseCase.ShouldRestoreName("Other", "New"));
    }
}
