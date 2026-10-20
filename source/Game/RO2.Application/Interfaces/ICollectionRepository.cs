using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for the per-character pet/monster Collection book (registered ids, selected skills,
/// active pet, fed-pet loyalties). One whole document per character. The register/select/feed/activate rules
/// and the ActivePetFlag derivation live in the Zone service; this interface is only the document read and
/// the whole-document upsert.
/// </summary>
public interface ICollectionRepository
{
 /// <summary>Loads the character document, or a fresh in-memory default (not yet stored) if none.</summary>
    Task<CollectionDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Upserts the whole document (create on first save, replace afterwards).</summary>
    Task SaveAsync(CollectionDocument doc, CancellationToken ct = default);
}
