namespace RO2.Parties.Application;

/// <summary>One roster member as the party-matching satellites read it: the character's last known state, online or not.</summary>
/// <param name="SessionId">The member's session ID (the roster key).</param>
/// <param name="CharacterId">The character's database ID (0 when the state is unknown).</param>
/// <param name="Gid">The GID the roster records carry (0 when the state is unknown).</param>
/// <param name="Name">The character name ("" when the state is unknown).</param>
/// <param name="MapId">The map the character stands on (0 when the state is unknown).</param>
/// <param name="Online">Whether the character is bound to a live in-world session.</param>
public readonly record struct PartyMember(uint SessionId, uint CharacterId, ulong Gid, string Name, uint MapId, bool Online);

/// <summary>A party's roster, in roster order, including offline members.</summary>
public sealed record PartyRoster(uint PartyId, ulong LeaderGid, uint LeaderSessionId, IReadOnlyList<PartyMember> Members);

/// <summary>Read-only party roster port shared by the party-matching satellites (ready check, dice). Implementations must not block.</summary>
public interface IPartyRoster
{
    /// <summary>The roster of the party the session is in, or null when it is in none.</summary>
    PartyRoster? RosterOf(uint sessionId);

    /// <summary>The roster of the party with that ID, or null when it no longer exists.</summary>
    PartyRoster? RosterOfParty(uint partyId);

    /// <summary>True while the session is still in <paramref name="partyId"/>.</summary>
    bool IsStillMember(uint partyId, uint sessionId);
}
