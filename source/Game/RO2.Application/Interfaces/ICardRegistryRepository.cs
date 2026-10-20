using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for each character's card registry: every card the character has registered, whether or not copies
/// are held.
/// </summary>
/// <remarks>
/// Registration rules live in the Zone host. This interface only reads and upserts the document. Callers read,
/// extend and replace it; there is no atomic registration.
/// </remarks>
public interface ICardRegistryRepository
{
 /// <summary>The character's registry, or an empty one (carrying <paramref name="characterId"/>) when it has none yet.</summary>
    Task<CardRegistryDocument> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Stamps <see cref="CardRegistryDocument.UpdatedAt"/> and replaces the character's document (upsert).</summary>
    Task SaveAsync(CardRegistryDocument doc, CancellationToken ct = default);
}
