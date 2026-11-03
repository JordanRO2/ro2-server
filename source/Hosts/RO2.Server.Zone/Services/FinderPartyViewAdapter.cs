using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>The finder's party read model over <see cref="PartyService"/> and <see cref="PlayerStateManager"/>.</summary>
public sealed class FinderPartyViewAdapter(PartyService parties, PlayerStateManager players) : IFinderPartyView
{
    public SearcherParty? PartyOf(uint searcherCharacterId)
    {
        if (players.GetPlayerByCharacterId(searcherCharacterId) is not { } searcher
            || parties.GetPlayerParty(searcher.SessionId) is not { } party) return null;
        var others = new List<PartySeat>();
        foreach (var memberId in party.MemberKeys)
            if (memberId != searcher.SessionId && parties.GetOnlineMember(party, memberId) is { } m)
                others.Add(new PartySeat(m.CharacterId, m.SessionId, m.Incarnation));
        return new SearcherParty(party.PartyId, others);
    }

    public bool IsStillMember(uint partyId, SeatKey seat) => parties.GetPlayerParty(seat.SessionId)?.PartyId == partyId;

    public uint? PartyIdOf(uint characterId) =>
        players.GetPlayerByCharacterId(characterId) is { } p ? parties.GetPlayerParty(p.SessionId)?.PartyId : null;
}
