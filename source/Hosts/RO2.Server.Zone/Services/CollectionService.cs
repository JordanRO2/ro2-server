using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
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

    
 /// <summary>Registers a monster collection (idempotent) and returns the new registered count.</summary>
    public async Task<int> RegisterMonsterAsync(uint characterId, uint collectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (collectionId != 0 && !doc.RegisteredMonsterIds.Contains(collectionId))
        {
            doc.RegisteredMonsterIds.Add(collectionId);
            await SaveAsync(doc, ct);
        }
        return doc.RegisteredMonsterIds.Count;
    }

 /// <summary>Registers a pet collection (idempotent) and returns the new registered count.</summary>
    public async Task<int> RegisterPetAsync(uint characterId, uint petCollectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (petCollectionId != 0 && !doc.RegisteredPetIds.Contains(petCollectionId))
        {
            doc.RegisteredPetIds.Add(petCollectionId);
            await SaveAsync(doc, ct);
        }
        return doc.RegisteredPetIds.Count;
    }

    
 /// <summary>
 /// Records a monster collection as the selected one. Selecting does not register it; registration happens with the first DNA feed.
 /// </summary>
 /// <returns>True if the collection is registered.</returns>
    public async Task<bool> SelectMonsterSkillAsync(uint characterId, uint collectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (collectionId != 0 && !doc.SelectedMonsterSkills.Contains(collectionId))
        {
            doc.SelectedMonsterSkills.Add(collectionId);
            await SaveAsync(doc, ct);
        }
        return doc.RegisteredMonsterIds.Contains(collectionId);
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

    
 /// <summary>
 /// Feeds one hatched egg to a pet already in the book. Its loyalty rises up to the pet's maximum loyalty level and the change is saved.
 /// </summary>
 /// <returns>The new loyalty, or null when nothing was fed: the pet is not in the book, has no data row, or is already at its maximum.</returns>
 /// <remarks>
 /// The caller consumes the egg only when a value is returned.
 /// </remarks>
    public async Task<ushort?> FeedPetAsync(uint characterId, uint petCollectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (!doc.RegisteredPetIds.Contains(petCollectionId)
            || _data is null || MonsterCollectionRules.Pet(_data, petCollectionId) is not { } pet)
            return null;
        if (PetCollectionBook.FedLoyalty(pet, doc.LoyaltyOf(petCollectionId)) is not { } fed)
            return null;
        doc.SetLoyalty(petCollectionId, fed);
        await SaveAsync(doc, ct);
        return fed;
    }

    
 /// <summary>
 /// Sets the active companion pet. An id of 0 clears the active pet. Only a pet already in the book can be made active, and activating never registers a pet.
 /// </summary>
 /// <returns>The stored pet id and its loyalty level, to echo back to the client.</returns>
    public async Task<(uint petId, byte flag)> SetActivePetAsync(uint characterId, uint petCollectionId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        if (petCollectionId != 0 && !doc.RegisteredPetIds.Contains(petCollectionId))
        {
            _log.LogWarning("Collection: char {Char} asked to activate pet {Pet}, which is not in its pet book; " +
                            "active pet stays {Active}", characterId, petCollectionId, doc.ActivePetId);
            return (doc.ActivePetId, doc.ActivePetFlag);
        }
        doc.ActivePetId = petCollectionId;
        doc.ActivePetFlag = PetCollectionBook.ActiveFlag(_data, petCollectionId, doc.LoyaltyOf(petCollectionId));
        await SaveAsync(doc, ct);
        return (doc.ActivePetId, doc.ActivePetFlag);
    }

 /// <summary>Reads the currently active pet (id and loyalty level) without changing state.</summary>
    public async Task<(uint petId, byte flag)> GetActivePetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await GetStateAsync(characterId, ct);
        return (doc.ActivePetId, doc.ActivePetFlag);
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
