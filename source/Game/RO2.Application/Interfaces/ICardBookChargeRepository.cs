namespace RO2.Application.Interfaces;

/// <summary>
/// Expiry of the card-book charge bought with card albums, per character. Rows are never deleted, so a book that
/// was never charged (null) and a lapsed one (a past instant) are distinct answers.
/// </summary>
public interface ICardBookChargeRepository
{
 /// <summary>The character's charge expiry in UTC, or null when its book was never charged.</summary>
    Task<DateTime?> GetChargedUntilUtcAsync(uint characterId, CancellationToken ct = default);

 /// <summary>
 /// Adds <paramref name="days"/> to the book's charge and returns the new expiry in UTC. A book still charged at
 /// <paramref name="nowUtc"/> is extended from its current expiry; a lapsed or never-charged book starts from
 /// <paramref name="nowUtc"/>.
 /// </summary>
 /// <remarks>Days are added on the server's local calendar, then stored as UTC.</remarks>
    Task<DateTime> ExtendChargeAsync(uint characterId, int days, DateTime nowUtc, CancellationToken ct = default);
}
