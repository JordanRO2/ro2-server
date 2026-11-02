using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

public class GuildInviteRulesDomainTests
{
    // Every combination of the seven facts must resolve in the contract order 7,8,54,3,1,73,11,0.
    [Fact]
    public void Decide_resolves_every_fact_combination_in_contract_order()
    {
        var end = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var m = 0; m < 64; m++)
        {
            var f = new GuildInviteRules.InviteFacts(
                (m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0, (m & 16) != 0, (m & 32) != 0 ? end : null, (m & 1) != 0 && (m & 2) == 0);
            var expected =
                !f.InviterInGuild ? (GuildInviteRules.InviterNoGuild, null)
                : !f.InviterMayInvite ? (GuildInviteRules.InviterNoRight, null)
                : !f.NameExists ? (GuildInviteRules.NoSuchName, null)
                : !f.InviteeOnline ? (GuildInviteRules.InviteeOffline, null)
                : f.InviteeInGuild ? (GuildInviteRules.InviteeInGuild, null)
                : f.RejoinAtUtc != null ? (GuildInviteRules.RejoinCooldownActive, f.RejoinAtUtc)
                : f.InviteePending ? (GuildInviteRules.AlreadyInvited, null)
                : (GuildInviteRules.Sent, (DateTime?)null);
            Assert.Equal(((ushort, DateTime?))expected, GuildInviteRules.Decide(f));
        }
    }
}
