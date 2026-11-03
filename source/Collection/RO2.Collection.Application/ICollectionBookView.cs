namespace RO2.Collections.Application;

/// <summary>
/// What the world-entry and warp bursts read of a character's collection book: the three counts and the active pet that
/// the 2913 / 2919 / 2918 carry. Read-only; the registers, feeds and the active-pet set stay in the collection services.
/// </summary>
public interface ICollectionBookView
{
    /// <summary>
    /// Loads the character's book. A storage error yields the empty book, never an exception. The active pet flag is
    /// derived from the pet's stored loyalty, not trusted from storage.
    /// </summary>
    Task<CollectionBook> GetBookAsync(uint characterId, CancellationToken ct = default);
}

/// <summary>The read model of <see cref="ICollectionBookView"/>.</summary>
/// <param name="RegisteredMonsterCount">How many monster collections are registered (2913).</param>
/// <param name="RegisteredPetCount">How many pet collections are registered (2919).</param>
/// <param name="ActivePetId">The active pet's collection id, or 0 for none.</param>
/// <param name="ActivePetFlag">The active pet's flag (2918); meaningful only when <paramref name="ActivePetId"/> is not 0.</param>
public sealed record CollectionBook(int RegisteredMonsterCount, int RegisteredPetCount, uint ActivePetId, byte ActivePetFlag);
