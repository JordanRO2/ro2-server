namespace RO2.DungeonFinder.Domain;

/// <summary>Which answer opcode a search is answered on.</summary>
public enum SearchKind { Normal, Random }

/// <summary>The transport an effect's frame travels on. Every round effect is <see cref="Internal"/>.</summary>
public enum FinderChannel { Internal, Rmi }

/// <summary>What the dungeon finder wants sent to one session. The host turns each effect into its frame.</summary>
public abstract record FinderEffect(uint SessionId)
{
    public virtual FinderChannel Channel => FinderChannel.Internal;
}

/// <summary>The searcher's answer to its search: <paramref name="Result"/> on the opcode <paramref name="Kind"/> picks.</summary>
public sealed record SearchAnswered(uint SessionId, ushort Result, SearchKind Kind) : FinderEffect(SessionId);

/// <summary>Asks a party member to pick a role.</summary>
public sealed record SelectRolePlease(uint SessionId) : FinderEffect(SessionId);

/// <summary>The round's filled slots and the recipient's own role.</summary>
public sealed record InProgress(uint SessionId, uint ListId, byte Tanks, byte Healers, byte Dealers, byte OwnRole)
    : FinderEffect(SessionId);

/// <summary>A member's answer to its role pick.</summary>
public sealed record SelectRoleAnswered(uint SessionId, ushort Result) : FinderEffect(SessionId);

/// <summary>The round ended without a match.</summary>
public sealed record RoundFailed(uint SessionId) : FinderEffect(SessionId);
