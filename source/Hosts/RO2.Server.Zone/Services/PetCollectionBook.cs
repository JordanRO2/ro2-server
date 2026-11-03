using System.Collections.Generic;
using RO2.Collections.Domain;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// A pet's collection record as the server holds it, shared by the collection packets: 2916 (book), 2917
/// (selected pet), 2918 (active pet) and 2920 (pet changed by 1627). Forwards to <see cref="CollectionRules"/>, resolving the
/// ambient pet table host-side.
/// </summary>
public static class PetCollectionBook
{
 /// <summary>Loyalty gained by feeding one egg to a pet already in the collection.</summary>
    public const ushort LoyaltyPerFeed = CollectionRules.LoyaltyPerFeed;

 /// <summary>Returns the collection record fields of a pet at the given loyalty.</summary>
    public static PetCollectionState State(GameDataStore? store, uint petId, ushort loyalty) =>
        CollectionRules.PetState(store?.PetInfo, petId, loyalty);

 /// <summary>Returns the book (2916): one record per registered pet, in ascending pet ID order, each at its stored loyalty.</summary>
    public static List<PetCollectionState> BookStates(GameDataStore? store, CollectionDocument book) =>
        CollectionRules.PetBookStates(store?.PetInfo, book);

 /// <summary>Returns the active-pet flag (2918): the active pet's loyalty level.</summary>
    public static byte ActiveFlag(GameDataStore? store, uint petId, ushort loyalty) =>
        CollectionRules.PetActiveFlag(store?.PetInfo, petId, loyalty);

 /// <summary>Returns the loyalty after feeding a pet one egg, or null when the pet is already at its loyalty level cap.</summary>
    public static ushort? FedLoyalty(PetInfo pet, ushort loyalty) => CollectionRules.FedLoyalty(pet, loyalty);
}
