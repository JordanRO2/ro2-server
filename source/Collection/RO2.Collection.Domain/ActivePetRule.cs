using RO2.GameData.Tables;

namespace RO2.Collections.Domain;

/// <summary>The outcome of a set-active-pet request: whether it was accepted, and the pet id and flag to store and echo back.</summary>
public readonly record struct ActivePetOutcome(bool Accepted, uint PetId, byte Flag);

/// <summary>
/// The pure validity rule of RMI 1629 (set the active pet). The 1629 answer carries result 0 even when the request is refused, so a refusal
/// still returns the unchanged pair to echo. The PetInfo rows come in as an argument; storage and the wire stay in the Zone host.
/// </summary>
public static class ActivePetRule
{
    /// <summary>
    /// Id 0 clears the active pet. Any other id is accepted only when the pet is already in the book (activating never registers a pet). When accepted,
    /// the flag is re-derived from the stored loyalty of the pet; when refused, the current active pet and flag of the book are returned.
    /// </summary>
    public static ActivePetOutcome Decide(IEnumerable<PetInfo>? rows, CollectionDocument book, uint requestedPetId)
    {
        if (requestedPetId != 0 && !book.RegisteredPetIds.Contains(requestedPetId))
            return new(false, book.ActivePetId, book.ActivePetFlag);
        return new(true, requestedPetId, CollectionRules.PetActiveFlag(rows, requestedPetId, book.LoyaltyOf(requestedPetId)));
    }
}
