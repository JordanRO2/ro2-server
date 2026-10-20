using Microsoft.Extensions.Logging.Abstractions;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The answer to C2S 1529 ReqPartyTacticsTarget: a one-entry 2751 carrying the requester's map, sent to every party member
/// in the world, the requester included, on any map and in any raid group (PartyTacticsTargetHandler.LiveRecipients).
/// </summary>
public class PartyTacticsTarget1529Tests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static (PlayerStateManager Players, PartyService Service) NewPartyService()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, NullLogger<BroadcastService>.Instance), NullLogger<PartyService>.Instance);
        return (players, service);
    }

    [Fact]
    public void LiveCopy_IsTheFrameRetailSentToTheOtherMembers()
    {
        Assert.Equal(Convert.FromHexString("0113130045e4000000001d0c0000010c0011a82c03"),
            PartyAnswerPackets.BuildAnsPartyTacticsTarget2751(3101, 12, 0x032CA811));
    }

    [Fact]
    public void ClearingAMark_IsAnsweredWithMarkZero()
    {
        Assert.Equal(Convert.FromHexString("0113130045e4000000005b04000001000011cb5409"),
            PartyAnswerPackets.BuildAnsPartyTacticsTarget2751(1115, 0, 0x0954CB11));
    }

 /// Twelve members over slots 1-12 in three raid groups, on the requester's map and on another map, one of them
 /// offline. Every member in the world receives the mark, in roster order, including the requester. The offline member
 /// has no session to receive it.
    [Fact]
    public void LiveRecipients_AreEveryMemberInTheWorld_OnAnyMapAndInAnyRaidGroup()
    {
        var (players, service) = NewPartyService();
        var members = new List<PlayerState>();
        for (uint i = 1; i <= 12; i++)
        {
            var p = new PlayerState
            {
                SessionId = 100 + i, CharacterUid = 0x0DE0B6B3A7000000UL + i, Name = "M" + i,
                MapId = i % 3 == 0 ? 1100u : 3101u,
            };
            members.Add(p);
            if (i != 7) players.AddPlayer(p.SessionId, p); 
        }
        var party = new PartyState
        {
            PartyId = 0x116115, LeaderSessionId = 101, FounderGid = members[0].CharacterUid,
            MemberSessionIds = members.Select(m => m.SessionId).ToList(),
        };
        byte slot = 1;
        foreach (var m in members)
        {
            party.Members[m.SessionId] = m;
            party.FrameSlots[m.SessionId] = slot++;
        }

        var expected = members.Where(m => m.SessionId != 107).Select(m => m.SessionId).ToList();
        Assert.Equal(expected, PartyTacticsTargetHandler.LiveRecipients(service, party));
    }

 /// A member key whose session now carries another character is not that member any more, so it gets no copy
 /// (PartyService.GetOnlineMember).
    [Fact]
    public void LiveRecipients_SkipASessionThatNowCarriesAnotherCharacter()
    {
        var (players, service) = NewPartyService();
        var requester = new PlayerState { SessionId = 1, CharacterUid = 0x0DE0B6B3A7000001UL, Name = "Req", MapId = 3101 };
        var joined = new PlayerState { SessionId = 2, CharacterUid = 0x0DE0B6B3A7000002UL, Name = "Joined", MapId = 3101 };
        var other = new PlayerState { SessionId = 2, CharacterUid = 0x0DE0B6B3A7000099UL, Name = "Other", MapId = 3101 };
        players.AddPlayer(1, requester);
        players.AddPlayer(2, other);
        var party = new PartyState
        {
            PartyId = 0x40A, LeaderSessionId = 1, FounderGid = requester.CharacterUid,
            MemberSessionIds = [1, 2],
        };
        party.Members[1] = requester;
        party.Members[2] = joined;
        party.FrameSlots[1] = 1;
        party.FrameSlots[2] = 2;

        Assert.Equal(new List<uint> { 1 }, PartyTacticsTargetHandler.LiveRecipients(service, party));
    }
}
