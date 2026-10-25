using System.Collections.Generic;
using System.Linq;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// A pet's collection record as the server holds it, shared by the collection packets: 2916 (book), 2917
/// (selected pet), 2918 (active pet) and 2920 (pet changed by 1627). Fields: pet ID, loyalty effect, loyalty
/// level and loyalty. An unfed pet has loyalty 0. A pet ID unknown to the pet data keeps only its ID.
/// </summary>
public static class PetCollectionBook
{
 /// <summary>Loyalty gained by feeding one egg to a pet already in the collection.</summary>
    public const ushort LoyaltyPerFeed = 10;

 /// <summary>Returns the collection record fields of a pet at the given loyalty.</summary>
    public static PetCollectionState State(GameDataStore? store, uint petId, ushort loyalty) =>
        store is not null && MonsterCollectionRules.Pet(store, petId) is { } pet
            ? MonsterCollectionRules.PetState(pet, loyalty)
            : new PetCollectionState(petId, 0, 0, 0);

 /// <summary>
 /// Returns the book (2916): one record per registered pet, in ascending pet ID order, each at its stored loyalty.
 /// </summary>
    public static List<PetCollectionState> BookStates(GameDataStore? store, CollectionDocument book) =>
        book.RegisteredPetIds.Where(id => id != 0).Distinct().OrderBy(id => id)
            .Select(id => State(store, id, book.LoyaltyOf(id)))
            .ToList();

 /// <summary>Returns the active-pet flag (2918): the active pet's loyalty level.</summary>
    public static byte ActiveFlag(GameDataStore? store, uint petId, ushort loyalty) =>
        State(store, petId, loyalty).LoyaltyLevel;

 /// <summary>
 /// Returns the loyalty after feeding a pet one egg, or null when the pet is already at its loyalty level cap.
 /// </summary>
    public static ushort? FedLoyalty(PetInfo pet, ushort loyalty)
    {
        var cap = pet.LoyaltyLevel_1;
        if (loyalty >= cap)
            return null;
        return (ushort)Math.Min(loyalty + LoyaltyPerFeed, cap);
    }
}
