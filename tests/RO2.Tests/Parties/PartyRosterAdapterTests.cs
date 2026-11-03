using Microsoft.Extensions.Logging.Abstractions;
using RO2.Parties.Application;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Parties;

/// <summary>The shared <see cref="IPartyRoster"/> port over <see cref="PartyService"/>: roster order, online flag, last known state and membership.</summary>
public class PartyRosterAdapterTests
{
    private sealed class NoPersistence : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static (PartyRosterAdapter Roster, PartyState Party) Build()
    {
        var players = new PlayerStateManager(new NoPersistence());
        var leader = new PlayerState { SessionId = 1, CharacterId = 11, CharacterUid = 0x0DE0B6B3A7000001UL, Name = "Lead", MapId = 1100 };
        var member = new PlayerState { SessionId = 2, CharacterId = 22, Name = "Mem", MapId = 1300 };
        players.AddPlayer(leader.SessionId, leader);
        // Session 3 never logged in: it stays in the roster with its last known state, offline.
        var away = new PlayerState { SessionId = 3, CharacterId = 33, CharacterUid = 0x0DE0B6B3A7000003UL, Name = "Away", MapId = 1200 };
        players.AddPlayer(member.SessionId, member);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState { PartyId = 7, LeaderSessionId = 1, MemberSessionIds = [1, 2, 3] };
        party.Members[1] = leader;
        party.Members[2] = member;
        party.Members[3] = away;
        parties.Track(party);
        return (new PartyRosterAdapter(parties), party);
    }

    [Fact]
    public void RosterOf_ListsEveryMemberInRosterOrderWithItsOnlineFlag()
    {
        var (roster, _) = Build();
        var r = roster.RosterOf(2)!;
        Assert.Equal(7u, r.PartyId);
        Assert.Equal(1u, r.LeaderSessionId);
        Assert.Equal(0x0DE0B6B3A7000001UL, r.LeaderGid);
        Assert.Equal(new uint[] { 1, 2, 3 }, r.Members.Select(m => m.SessionId));
        Assert.Equal(new[] { true, true, false }, r.Members.Select(m => m.Online));
        Assert.Equal(new[] { "Lead", "Mem", "Away" }, r.Members.Select(m => m.Name));
        Assert.Equal(new uint[] { 11, 22, 33 }, r.Members.Select(m => m.CharacterId));
        Assert.Equal(new uint[] { 1100, 1300, 1200 }, r.Members.Select(m => m.MapId));
        Assert.Equal(0x0DE0B6B3A7000003UL, r.Members[2].Gid);
        Assert.Equal(22UL, r.Members[1].Gid); // no CharacterUid: the GID falls back to the character ID
    }

    [Fact]
    public void RosterOfParty_AndIsStillMember_FollowThePartyId()
    {
        var (roster, _) = Build();
        Assert.Equal(7u, roster.RosterOfParty(7)!.PartyId);
        Assert.Null(roster.RosterOfParty(8));
        Assert.Null(roster.RosterOf(99));
        Assert.True(roster.IsStillMember(7, 1));
        Assert.False(roster.IsStillMember(8, 1));
        Assert.False(roster.IsStillMember(7, 99));
    }
}
