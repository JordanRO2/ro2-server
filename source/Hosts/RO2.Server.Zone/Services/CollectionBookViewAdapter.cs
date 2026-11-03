using RO2.Collections.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Host implementation of <see cref="ICollectionBookView"/> over <see cref="CollectionService.GetStateAsync"/>: the same values the readers took from the document.</summary>
public sealed class CollectionBookViewAdapter(CollectionService service) : ICollectionBookView
{
    /// <inheritdoc />
    public async Task<CollectionBook> GetBookAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await service.GetStateAsync(characterId, ct);
        return new CollectionBook(doc.RegisteredMonsterIds.Count, doc.RegisteredPetIds.Count, doc.ActivePetId, doc.ActivePetFlag);
    }
}
