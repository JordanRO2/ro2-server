using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for a character's card book, one document per character. The whole document is loaded and saved
/// as a unit.
/// </summary>
/// <remarks>The put and take-off rules are implemented by the caller.</remarks>
public interface ICardBookRepository
{
 /// <summary>Loads the character's card book, or a fresh in-memory default (not yet stored) if none.</summary>
    Task<CardBookDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Upserts the whole document (create on first save, replace afterwards).</summary>
    Task SaveAsync(CardBookDocument doc, CancellationToken ct = default);
}
