using RO2.Parties.Application;
using RO2.PartyDice.Domain;

namespace RO2.PartyDice.Application;

/// <summary>Turns the shared party roster into the inputs a <see cref="DiceRound"/> decides on.</summary>
public static class PartyDiceRoster
{
    /// <summary>
    /// The roster members who take part in a roll for a corpse on <paramref name="corpseMapId"/>: online, on that map,
    /// with a live session, in roster order. Empty when the party is gone.
    /// </summary>
    public static List<DiceSeat> Seats(PartyRoster? roster, uint corpseMapId, Func<uint, bool> hasSession)
        => roster is null ? [] : [.. roster.Members
            .Where(m => m.Online && m.MapId == corpseMapId && hasSession(m.SessionId))
            .Select(m => new DiceSeat(m.SessionId, m.Gid))];

    /// <summary>
    /// Whether a seat is still in the roll's party and online as the character that was asked, as of the roster read
    /// now. Compared by party ID and GID, never by the party's object identity.
    /// </summary>
    public static Func<DiceSeat, bool> StillIn(IPartyRoster parties, uint partyId)
    {
        var roster = parties.RosterOfParty(partyId);
        return seat => roster is not null && parties.IsStillMember(partyId, seat.SessionId)
            && roster.Members.Any(m => m.SessionId == seat.SessionId && m.Online && m.Gid == seat.Gid);
    }
}
