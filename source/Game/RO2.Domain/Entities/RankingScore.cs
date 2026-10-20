namespace RO2.Domain.Entities;

/// <summary>One character's score on one ranking type for one period.</summary>
/// <remarks><see cref="ReachedUtc"/> is when the score last changed and breaks ties.</remarks>
public sealed record RankingScore(uint CharId, long Score, DateTime ReachedUtc);
