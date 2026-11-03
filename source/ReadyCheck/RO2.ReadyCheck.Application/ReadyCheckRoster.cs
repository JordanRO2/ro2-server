using RO2.Parties.Application;
using RO2.ReadyChecks.Domain;

namespace RO2.ReadyChecks.Application;

/// <summary>Turns the shared party roster into the inputs a <see cref="ReadyCheck"/> decides on.</summary>
public static class ReadyCheckRoster
{
    /// <summary>The roster in roster order, offline members included; only online members can answer. Empty when the party is gone.</summary>
    public static List<RosterEntry> Entries(PartyRoster? roster)
        => roster is null ? [] : [.. roster.Members.Select(m => new RosterEntry(m.Gid, m.Name, m.Online))];

    /// <summary>The roster's member sessions in roster order. Empty when the party is gone.</summary>
    public static List<uint> SessionIds(PartyRoster? roster) => roster is null ? [] : [.. roster.Members.Select(m => m.SessionId)];

    /// <summary>Whether the session's member is online in the roster.</summary>
    public static bool IsOnline(PartyRoster? roster, uint sessionId)
        => roster is not null && roster.Members.Any(m => m.SessionId == sessionId && m.Online);

    /// <summary>
    /// Whether the initiator is still in the check's party: the party exists, the session is still a member of it, and
    /// it is online as the same character (by GID). Compared by party ID and GID, never by the party's object identity.
    /// </summary>
    public static bool InitiatorPresent(IPartyRoster parties, uint partyId, uint initiatorSessionId, ulong initiatorGid)
        => parties.RosterOfParty(partyId) is { } roster
           && parties.IsStillMember(partyId, initiatorSessionId)
           && roster.Members.Any(m => m.SessionId == initiatorSessionId && m.Gid == initiatorGid && m.Online);
}
