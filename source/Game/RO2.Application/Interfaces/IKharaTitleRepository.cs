using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for a character's selected Khara titles (one <see cref="KharaTitleDocument"/> per
/// character). The title rules, the published-titles cache and the play-clock reads live in the Zone service;
/// this interface is only the Mongo read, the upsert and the play-clock update.
/// </summary>
public interface IKharaTitleRepository
{
 /// <summary>The character's title document, or null when it never selected a title on this server.</summary>
    Task<KharaTitleDocument?> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Upserts the character's whole title document (create on first save).</summary>
    Task SaveAsync(KharaTitleDocument doc, CancellationToken ct = default);

 /// <summary>
 /// Stores where the character's play clock stands into its existing title document (PlayClockUtc and
 /// UpdatedUtc only). A character with no document gets none (no upsert).
 /// </summary>
    Task StorePlayClockAsync(uint characterId, DateTime playClockUtc, DateTime updatedUtc, CancellationToken ct = default);
}
