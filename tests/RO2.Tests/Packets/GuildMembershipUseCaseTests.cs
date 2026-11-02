using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure membership decisions (RO2.Guild.Application): invite, answer, kick, leave, set-auth, handover.</summary>
public class GuildMembershipUseCaseTests
{
    [Theory]
    [InlineData(false, true, true)]    // offline invitee + inviter with the right: look the name up
    [InlineData(false, false, false)]  // a refused inviter never reaches the name
    [InlineData(true, true, false)]    // online invitee: the name is known
    public void Invite_name_lookup_only_for_offline_invitee_with_right(bool online, bool mayInvite, bool expected)
        => Assert.Equal(expected, GuildInviteUseCase.NeedsNameLookup(online, mayInvite));

    [Fact]
    public void Invite_settle_turns_a_refused_registration_into_already_invited_only_for_sent()
    {
        Assert.Equal(GuildInviteRules.AlreadyInvited, GuildInviteUseCase.Settle(GuildInviteRules.Sent, inviteRegistered: false));
        Assert.Equal(GuildInviteRules.Sent, GuildInviteUseCase.Settle(GuildInviteRules.Sent, inviteRegistered: true));
        Assert.Equal(GuildInviteRules.InviterNoRight, GuildInviteUseCase.Settle(GuildInviteRules.InviterNoRight, inviteRegistered: false));
        Assert.True(GuildInviteUseCase.ShouldNotifyInvitee(GuildInviteRules.Sent));
        Assert.False(GuildInviteUseCase.ShouldNotifyInvitee(GuildInviteRules.AlreadyInvited));
    }

    [Theory]
    [InlineData(true, true, false, InviteAnswerOutcome.Join, 0, true)]
    [InlineData(true, true, true, InviteAnswerOutcome.Refuse, 1, false)]   // accept while in a guild: no notice to the inviter
    [InlineData(true, false, false, InviteAnswerOutcome.Refuse, 12, false)]
    [InlineData(false, true, false, InviteAnswerOutcome.Refuse, 0, true)]  // decline of a valid invite is reported
    [InlineData(false, true, true, InviteAnswerOutcome.Refuse, 0, true)]
    [InlineData(false, false, false, InviteAnswerOutcome.Refuse, 12, false)]
    public void Answer_decision(bool accepted, bool valid, bool inGuild, InviteAnswerOutcome outcome, int code, bool notify)
    {
        var d = GuildInviteAnswerUseCase.Decide(accepted, valid, inGuild);
        Assert.Equal(outcome, d.Outcome);
        Assert.Equal((ushort)code, d.Code);
        Assert.Equal(notify, d.NotifyInviter);
    }

    [Theory]
    [InlineData(false, true, false, 7)]   // no guild beats everything
    [InlineData(true, false, true, 8)]    // no right beats self
    [InlineData(true, true, true, 24)]
    [InlineData(true, true, false, 0)]
    public void Kick_kicker_checks_run_guild_right_self(bool inGuild, bool mayKick, bool self, int expected)
        => Assert.Equal((ushort)expected, GuildKickUseCase.CheckKicker(inGuild, mayKick, self));

    [Fact]
    public void Kick_target_and_settle_codes()
    {
        Assert.Equal(GuildKickUseCase.ResultNoSuchMember, GuildKickUseCase.CheckTarget(false, true));
        Assert.Equal(GuildKickUseCase.ResultNoRight, GuildKickUseCase.CheckTarget(true, true));   // the master is never kickable
        Assert.Equal(GuildKickUseCase.ResultOk, GuildKickUseCase.CheckTarget(true, false));
        Assert.Equal(GuildKickUseCase.ResultOk, GuildKickUseCase.Settle(true));
        Assert.Equal(GuildKickUseCase.ResultNoSuchMember, GuildKickUseCase.Settle(false));
    }

    [Fact]
    public void Leave_checks_settle_and_aftermath()
    {
        Assert.Equal(GuildLeaveUseCase.ResultNoGuild, GuildLeaveUseCase.Check(false, false));
        Assert.Equal(GuildLeaveUseCase.ResultMasterCannotLeave, GuildLeaveUseCase.Check(true, true));
        Assert.Equal(GuildLeaveUseCase.ResultOk, GuildLeaveUseCase.Check(true, false));
        Assert.Equal(GuildLeaveUseCase.ResultNoGuild, GuildLeaveUseCase.Settle(false));
        Assert.Equal(LeaveAftermath.Disband, GuildLeaveUseCase.Aftermath(0));
        Assert.Equal(LeaveAftermath.NotifyRemaining, GuildLeaveUseCase.Aftermath(1));
    }

    [Fact]
    public void SetAuth_and_handover_codes()
    {
        Assert.Equal((ushort)1, GuildSetAuthUseCase.Check(false, false));
        Assert.Equal((ushort)2, GuildSetAuthUseCase.Check(true, false));
        Assert.Equal((ushort)0, GuildSetAuthUseCase.Check(true, true));
        Assert.Equal((ushort)3, GuildSetAuthUseCase.Settle(false));
        Assert.Equal((ushort)7, GuildMasterHandoverUseCase.Precheck(false, true));
        Assert.Equal((ushort)17, GuildMasterHandoverUseCase.Precheck(true, false));
        Assert.Equal((ushort)0, GuildMasterHandoverUseCase.Precheck(true, true));
        Assert.Equal((ushort)13, GuildMasterHandoverUseCase.Settle(false));
    }
}
