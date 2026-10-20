namespace RO2.Domain.Entities;

/// <summary>A character's stored union membership: the union id and the join time in Unix seconds.</summary>
public readonly record struct UnionMembership(uint UnionId, long JoinedAtUnix);
