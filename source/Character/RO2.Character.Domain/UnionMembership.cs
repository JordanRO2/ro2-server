namespace RO2.Characters.Domain;

/// <summary>A character's stored union membership: the union id and the join time in Unix seconds.</summary>
/// <param name="UnionId">The union's id.</param>
/// <param name="JoinedAtUnix">Join time in Unix seconds.</param>
public readonly record struct UnionMembership(uint UnionId, long JoinedAtUnix);
