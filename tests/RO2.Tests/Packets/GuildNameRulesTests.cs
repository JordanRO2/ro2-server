using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure guild-name format rules (RO2.Guild.Domain), no host.</summary>
public class GuildNameRulesTests
{
    [Theory]
    [InlineData("", GuildNameVerdict.TooShort)]
    [InlineData("a", GuildNameVerdict.TooShort)]
    [InlineData("ab", GuildNameVerdict.Ok)]
    [InlineData("12345678901234", GuildNameVerdict.Ok)]
    [InlineData("123456789012345", GuildNameVerdict.TooLong)]
    [InlineData("-Name", GuildNameVerdict.LeadingSymbol)]
    [InlineData(" Name", GuildNameVerdict.Ok)]
    [InlineData("-Æsir", GuildNameVerdict.LeadingSymbol)]
    [InlineData("ßlueßlooD", GuildNameVerdict.Ok)]
    [InlineData("Æsir-", GuildNameVerdict.Ok)]
    [InlineData("a<b", GuildNameVerdict.ForbiddenChar)]
    [InlineData("ab>", GuildNameVerdict.ForbiddenChar)]
    [InlineData("aab", GuildNameVerdict.Ok)]
    [InlineData("baaa", GuildNameVerdict.RepeatedChar)]
    [InlineData("aaab", GuildNameVerdict.RepeatedChar)]
    public void CheckFormat_matches_the_rules(string name, GuildNameVerdict expected)
        => Assert.Equal(expected, GuildNameRules.CheckFormat(name));
}
