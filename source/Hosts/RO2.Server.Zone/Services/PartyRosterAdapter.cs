using RO2.Parties.Application;

namespace RO2.Server.Zone.Services;

/// <summary>The party-matching satellites' roster read model over <see cref="PartyService"/>.</summary>
public sealed class PartyRosterAdapter(PartyService parties) : IPartyRoster
{
    public PartyRoster? RosterOf(uint sessionId) => parties.GetPlayerParty(sessionId) is { } party ? Snapshot(party) : null;

    public PartyRoster? RosterOfParty(uint partyId) => parties.GetParty(partyId) is { } party ? Snapshot(party) : null;

    public bool IsStillMember(uint partyId, uint sessionId) => parties.GetPlayerParty(sessionId)?.PartyId == partyId;

    private PartyRoster Snapshot(PartyState party)
    {
        var members = new List<PartyMember>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            var online = parties.GetOnlineMember(party, memberId);
            var known = online ?? party.Members.GetValueOrDefault(memberId);
            members.Add(new PartyMember(memberId, known?.CharacterId ?? 0, PartyAnswerPackets.MemberGid(known),
                known?.Name ?? "", known?.MapId ?? 0, online is not null));
        }
        var leader = parties.GetOnlineMember(party, party.LeaderSessionId) ?? party.Members.GetValueOrDefault(party.LeaderSessionId);
        return new PartyRoster(party.PartyId, PartyAnswerPackets.MemberGid(leader), party.LeaderSessionId, members);
    }
}
