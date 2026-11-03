using RO2.Parties.Application;
using RO2.ReadyChecks.Application;
using RO2.ReadyChecks.Domain;
using Xunit;

namespace RO2.Tests.ReadyChecks;

/// <summary>The <see cref="ReadyCheck"/> aggregate: Start, Vote and Settle decisions and their effect order.</summary>
public class ReadyCheckAggregateTests
{
    private const uint Party = 5;
    private const uint LeadSession = 10;
    private const ulong LeadGid = 1;

    private static ReadyCheck NewCheck() => new(Party, LeadSession, LeadGid);

    private static RosterEntry[] Roster(params (ulong Gid, string Name, bool CanAnswer)[] m)
        => [new(LeadGid, "Lead", true), .. m.Select(x => new RosterEntry(x.Gid, x.Name, x.CanAnswer))];

    [Fact]
    public void Start_AcksTheLeaderThenNotifiesEachOnlineMemberInRosterOrder()
    {
        var effects = NewCheck().Start(false, [10, 30, 20, 40], id => id != 40);
        Assert.Equal(new ReadyCheckEffect[] { new AckOk(), new NotifyStart(30, LeadGid), new NotifyStart(20, LeadGid) }, effects);
    }

    [Fact]
    public void Start_WhileAnotherCheckRuns_IsOnlyAlreadyChecking()
        => Assert.Equal(new ReadyCheckEffect[] { new AlreadyChecking() }, NewCheck().Start(true, [10, 20], _ => true));

    [Fact]
    public void Vote_FirstIsAcked_SecondIsAlreadyVotedAndTheFirstStands()
    {
        var check = NewCheck();
        Assert.Equal(new ReadyCheckEffect[] { new AckOk() }, check.Vote(2, ReadyCheck.VoteReady));
        Assert.Equal(new ReadyCheckEffect[] { new AlreadyVoted() }, check.Vote(2, 0));
        var result = Assert.IsType<CheckResult>(Assert.Single(check.Settle(Roster((2, "Yes", true)), false, true)));
        Assert.Equal((1, 0), (result.Ready, result.NotReady));
    }

    [Fact]
    public void Vote_IsIgnoredForTheInitiatorAnUnknownGidAndAfterSettling()
    {
        var check = NewCheck();
        Assert.Empty(check.Vote(LeadGid, 1));
        Assert.Empty(check.Vote(0, 1));
        check.Settle(Roster(), false, true);
        Assert.Empty(check.Vote(2, 1));
    }

    [Fact]
    public void Settle_WaitsForEveryAnswerableMemberButNotForOfflineOnes()
    {
        var check = NewCheck();
        var roster = Roster((2, "A", true), (3, "B", true), (4, "Off", false));
        Assert.Empty(check.Settle(roster, false, true));
        check.Vote(2, 1);
        Assert.Empty(check.Settle(roster, false, true));
        check.Vote(3, 0);
        var result = Assert.IsType<CheckResult>(Assert.Single(check.Settle(roster, false, true)));
        Assert.Equal((1, 2), (result.Ready, result.NotReady));
        Assert.Equal(new[] { "B", "Off" }, result.NotReadyNames);
    }

    [Fact]
    public void Settle_OnTimeoutCountsTheSilentAsNotReady()
    {
        var check = NewCheck();
        var result = Assert.IsType<CheckResult>(Assert.Single(check.Settle(Roster((2, "A", true)), true, true)));
        Assert.Equal((0, 1), (result.Ready, result.NotReady));
        Assert.Equal(new[] { "A" }, result.NotReadyNames);
    }

    [Fact]
    public void Settle_IsIdempotent_ATimeoutAfterASettleEmitsNothing()
    {
        var check = NewCheck();
        var roster = Roster((2, "A", true));
        check.Vote(2, 1);
        Assert.Single(check.Settle(roster, false, true));
        Assert.True(check.Finished);
        Assert.Empty(check.Settle(roster, true, true));
        Assert.Empty(check.Settle(roster, false, true));
    }

    [Fact]
    public void Settle_WhenTheInitiatorLeft_IsAbandonedAndStillFinishes()
    {
        var check = NewCheck();
        Assert.Equal(new ReadyCheckEffect[] { new Abandoned() }, check.Settle(Roster(), true, initiatorPresent: false));
        Assert.True(check.Finished);
        Assert.Empty(check.Settle(Roster(), true, true));
    }

    private static PartyRoster Snapshot(params PartyMember[] members) => new(Party, 0, LeadSession, members);

    [Fact]
    public void Roster_MapsPartyMembersToEntriesInOrder()
    {
        var roster = Snapshot(new PartyMember(10, 1, LeadGid, "Lead", 1100, true), new PartyMember(20, 2, 2, "Away", 1100, false));
        Assert.Equal(new[] { new RosterEntry(LeadGid, "Lead", true), new RosterEntry(2, "Away", false) }, ReadyCheckRoster.Entries(roster));
        Assert.Equal(new uint[] { 10, 20 }, ReadyCheckRoster.SessionIds(roster));
        Assert.True(ReadyCheckRoster.IsOnline(roster, 10));
        Assert.False(ReadyCheckRoster.IsOnline(roster, 20));
        Assert.Empty(ReadyCheckRoster.Entries(null));
    }

    private sealed class FakeRoster(PartyRoster? party, bool stillMember) : IPartyRoster
    {
        public PartyRoster? RosterOf(uint sessionId) => party;
        public PartyRoster? RosterOfParty(uint partyId) => partyId == Party ? party : null;
        public bool IsStillMember(uint partyId, uint sessionId) => stillMember && partyId == Party;
    }

    [Fact]
    public void InitiatorPresent_IsPartyIdAndGidEquality()
    {
        var online = Snapshot(new PartyMember(LeadSession, 1, LeadGid, "Lead", 1100, true));
        Assert.True(ReadyCheckRoster.InitiatorPresent(new FakeRoster(online, true), Party, LeadSession, LeadGid));
        // A different character on the same session (a character switch), the session gone, the party gone, or offline.
        Assert.False(ReadyCheckRoster.InitiatorPresent(new FakeRoster(online, true), Party, LeadSession, 99));
        Assert.False(ReadyCheckRoster.InitiatorPresent(new FakeRoster(online, false), Party, LeadSession, LeadGid));
        Assert.False(ReadyCheckRoster.InitiatorPresent(new FakeRoster(null, true), Party, LeadSession, LeadGid));
        Assert.False(ReadyCheckRoster.InitiatorPresent(new FakeRoster(online, true), 6, LeadSession, LeadGid));
        var offline = Snapshot(new PartyMember(LeadSession, 1, LeadGid, "Lead", 1100, false));
        Assert.False(ReadyCheckRoster.InitiatorPresent(new FakeRoster(offline, true), Party, LeadSession, LeadGid));
    }
}
