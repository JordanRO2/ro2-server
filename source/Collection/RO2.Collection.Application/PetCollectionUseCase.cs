using RO2.Collections.Domain;
using RO2.GameData.Tables;

namespace RO2.Collections.Application;

/// <summary>Which storage call of <see cref="PetCollectionUseCase"/> failed; the host logs it and carries on.</summary>
public enum PetCollectionStorageOp
{
    /// <summary>The collection book load threw; the flow went on with an empty book.</summary>
    BookLoad,
    /// <summary>The collection book save threw; the change is lost.</summary>
    BookSave,
}

/// <summary>
/// The outcome of one egg use (1627): <see cref="Kind"/> says whether the pet was registered, fed or refused at the loyalty cap.
/// <see cref="Loyalty"/> is the new loyalty when fed and the loyalty read from the book when refused (0 when registered).
/// <see cref="Registered"/> is the registered-pet count for the 2919: after the registration, or of the book as read before a feed.
/// <see cref="WasActive"/> says whether the fed pet is the book's active pet (the host then also sends the 2918).
/// </summary>
public readonly record struct PetEggUse(PetEggKind Kind, ushort Loyalty, int Registered, bool WasActive);

/// <summary>
/// The pet-collection flows over the book: the 1627 egg use (register the pet, or feed it up to its loyalty cap, decided with
/// <see cref="PetEggDecision"/>), the 1629 set-active (validated with <see cref="ActivePetRule"/>) and the 1628 active-pet read. Storage errors are
/// reported through <c>onStorageError</c> and swallowed (a load error yields an empty book, a save error drops the change), so the client is still
/// answered. The PetInfo rows come in as a constructor argument and the active-pet flag is re-derived from the stored loyalty on every load. The bag
/// check, the egg consume, the wire sends and their order stay in the Zone host.
/// </summary>
/// <remarks>
/// Every update is a whole-document read-modify-write with no compare-and-set, and the egg use reloads the book for each step (read, then register or
/// feed): two concurrent feeds of one pet can lose a feed, and the host takes the egg after the loyalty is saved (known, preserved).
/// </remarks>
public sealed class PetCollectionUseCase(
    ICollectionRepository book,
    IEnumerable<PetInfo>? petInfo,
    Action<PetCollectionStorageOp, uint, Exception>? onStorageError = null)
{
    /// <summary>
    /// Uses one hatched egg on <paramref name="petId"/>: a pet not in the book is registered (loyalty 0), a registered pet is fed up to its cap, and a
    /// pet at its cap (or one that is no longer in the book or has no row when the feed reloads) is refused with nothing written.
    /// </summary>
    public async Task<PetEggUse> UseEggAsync(uint characterId, uint petId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        if (!doc.RegisteredPetIds.Contains(petId))
            return new(PetEggKind.Registered, 0, await RegisterAsync(characterId, petId, ct), false);

        return await FeedAsync(characterId, petId, ct) is { } loyalty
            ? new(PetEggKind.Fed, loyalty, doc.RegisteredPetIds.Count, doc.ActivePetId == petId)
            : new(PetEggKind.Capped, doc.LoyaltyOf(petId), doc.RegisteredPetIds.Count, false);
    }

    /// <summary>Registers a pet (idempotent; id 0 is never registered) and returns the registered count. Reloads the book.</summary>
    public async Task<int> RegisterAsync(uint characterId, uint petId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        if (petId != 0 && !doc.RegisteredPetIds.Contains(petId))
        {
            doc.RegisteredPetIds.Add(petId);
            await SaveBookAsync(doc, ct);
        }
        return doc.RegisteredPetIds.Count;
    }

    /// <summary>
    /// Feeds one egg to a pet already in the book: its loyalty rises up to the pet's cap and the change is saved. Reloads the book.
    /// </summary>
    /// <returns>The new loyalty, or null when nothing was fed: the pet is not in the book, has no PetInfo row, or is already at its cap.</returns>
    public async Task<ushort?> FeedAsync(uint characterId, uint petId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        if (!doc.RegisteredPetIds.Contains(petId)
            || petInfo is null || CollectionRules.Pet(petInfo, petId) is not { } pet)
            return null;
        var decision = PetEggDecision.Decide(pet, registered: true, doc.LoyaltyOf(petId));
        if (decision.Kind != PetEggKind.Fed)
            return null;
        doc.SetLoyalty(petId, decision.Loyalty);
        await SaveBookAsync(doc, ct);
        return decision.Loyalty;
    }

    /// <summary>
    /// Sets the active pet (id 0 clears it); only a pet in the book can be made active, and activating never registers one. Saves only an accepted request.
    /// </summary>
    /// <returns>The pet id and loyalty-level flag to echo to the client. A refusal still answers (result 0) with the unchanged current pair, <see cref="ActivePetOutcome.Accepted"/> false.</returns>
    public async Task<ActivePetOutcome> SetActiveAsync(uint characterId, uint petId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        var decision = ActivePetRule.Decide(petInfo, doc, petId);
        if (!decision.Accepted)
            return decision;
        doc.ActivePetId = decision.PetId;
        doc.ActivePetFlag = decision.Flag;
        await SaveBookAsync(doc, ct);
        return decision;
    }

    /// <summary>Reads the active pet (id and loyalty-level flag, re-derived from the stored loyalty) without changing state.</summary>
    public async Task<(uint PetId, byte Flag)> GetActiveAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await LoadBookAsync(characterId, ct);
        return (doc.ActivePetId, doc.ActivePetFlag);
    }

    private async Task<CollectionDocument> LoadBookAsync(uint characterId, CancellationToken ct)
    {
        CollectionDocument doc;
        try
        {
            doc = await book.GetOrCreateAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(PetCollectionStorageOp.BookLoad, characterId, ex);
            doc = new CollectionDocument { Id = characterId };
        }
        doc.ActivePetFlag = CollectionRules.PetActiveFlag(petInfo, doc.ActivePetId, doc.LoyaltyOf(doc.ActivePetId));
        return doc;
    }

    private async Task SaveBookAsync(CollectionDocument doc, CancellationToken ct)
    {
        try
        {
            await book.SaveAsync(doc, ct);
        }
        catch (Exception ex)
        {
            onStorageError?.Invoke(PetCollectionStorageOp.BookSave, doc.Id, ex);
        }
    }
}
