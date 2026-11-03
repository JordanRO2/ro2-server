using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Collections.Domain;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Business logic for the monster and pet collection system. It stores each character's progress (registered collections, selected skills, active pet) through <see cref="ICollectionRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service is not registered in DI. Handlers create it with <see cref="From"/>, passing the scoped services they already hold.
/// </para>
/// <para>
/// Storage errors are logged and swallowed, so a database failure does not disconnect the client. The caller still sends its acknowledgement.
/// </para>
/// </remarks>
public sealed class CollectionService
{
    private readonly ICollectionRepository _repo;
    private readonly GameDataStore? _data;
    private readonly ILogger _log;

    public CollectionService(ICollectionRepository repo, GameDataStore? data, ILogger log)
    {
        _repo = repo;
        _data = data;
        _log = log;
    }

 /// <summary>Creates a <see cref="CollectionService"/> from a handler scope and logger.</summary>
    public static CollectionService From(IServiceProvider services, ILogger log)
    {
        return new CollectionService(services.GetRequiredService<ICollectionRepository>(), services.GetRequiredService<GameDataStore>(), log);
    }

 /// <summary>
 /// Loads the character's collection state, or returns defaults when none is stored. The active pet flag is always derived from the pet's stored loyalty, not trusted from storage.
 /// </summary>
    public async Task<CollectionDocument> GetStateAsync(uint characterId, CancellationToken ct = default)
    {
        CollectionDocument doc;
        try
        {
            doc = await _repo.GetOrCreateAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collection: load failed for char {Char}; using empty state", characterId);
            doc = new CollectionDocument { Id = characterId };
        }
        doc.ActivePetFlag = PetCollectionBook.ActiveFlag(_data, doc.ActivePetId, doc.LoyaltyOf(doc.ActivePetId));
        return doc;
    }


    
 /// <summary>
 /// Records a pet collection as the selected one. Selecting does not register it.
 /// </summary>
 /// <returns>The collection state after the selection, including the pet's loyalty.</returns>
    public async Task<CollectionDocument> SelectPetSkillAsync(uint characterId, uint petCollectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (petCollectionId != 0 && !doc.SelectedPetSkills.Contains(petCollectionId))
        {
            doc.SelectedPetSkills.Add(petCollectionId);
            await SaveAsync(doc, ct);
        }
        return doc;
    }

    
    private async Task SaveAsync(CollectionDocument doc, CancellationToken ct)
    {
        try
        {
            await _repo.SaveAsync(doc, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Collection: save failed for char {Char}", doc.Id);
        }
    }
}
