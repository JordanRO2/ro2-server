namespace RO2.Domain.Entities;

/// <summary>
/// One document per character holding all pet/monster Collection progress. Persisted to the
/// <c>collections</c> collection in the local dev Mongo (db ro2_server). <see cref="Id"/> maps to Mongo
/// <c>_id</c> and is the character id (uint) - the same <c>Id -&gt; _id</c> convention the Character
/// entity uses - so there is exactly one row per character and lookups are by primary key.
/// </summary>
public sealed class CollectionDocument
{
 /// <summary>Character id. Maps to Mongo _id (one collection doc per character).</summary>
    public uint Id { get; set; }

 /// <summary>Monster-collection ids the character has registered (completed) in the monster book.</summary>
    public List<uint> RegisteredMonsterIds { get; set; } = new();

 /// <summary>Pet-collection ids the character has registered in the pet book.</summary>
    public List<uint> RegisteredPetIds { get; set; } = new();

 /// <summary>Monster-collection ids whose granted skill the character has selected/active.</summary>
    public List<uint> SelectedMonsterSkills { get; set; } = new();

 /// <summary>Pet-collection ids whose granted skill the character has selected/active.</summary>
    public List<uint> SelectedPetSkills { get; set; } = new();

 /// <summary>Active companion pet collection id (0 = none).</summary>
    public uint ActivePetId { get; set; }

 /// <summary>Active-pet flag byte echoed to the client: the active pet's loyalty LEVEL, re-derived from
 /// <see cref="PetLoyalties"/> on every load (CollectionService.GetStateAsync), never trusted from storage.</summary>
    public byte ActivePetFlag { get; set; }

 /// <summary>
 /// The loyalty of each booked pet that has been fed (a pet with no entry is at 0, retail's unfed record).
 /// A list of <see cref="PetLoyalty"/> rather than a uint-keyed dictionary: the Mongo driver's default
 /// dictionary representation is a sub-document, whose keys must be strings. Documents stored before this
 /// field existed load it as empty (a missing element keeps the initialiser).
 /// </summary>
    public List<PetLoyalty> PetLoyalties { get; set; } = new();

 /// <summary>The stored loyalty of <paramref name="petId"/>, 0 when it was never fed (or is not booked).</summary>
    public ushort LoyaltyOf(uint petId)
    {
        foreach (var p in PetLoyalties)
            if (p.PetId == petId) return (ushort)Math.Clamp(p.Loyalty, 0, ushort.MaxValue);
        return 0;
    }

 /// <summary>Sets <paramref name="petId"/>'s stored loyalty, adding its entry on the first feed.</summary>
    public void SetLoyalty(uint petId, ushort loyalty)
    {
        foreach (var p in PetLoyalties)
        {
            if (p.PetId != petId) continue;
            p.Loyalty = loyalty;
            return;
        }
        PetLoyalties.Add(new PetLoyalty { PetId = petId, Loyalty = loyalty });
    }
}

/// <summary>One fed pet's loyalty: the value the 2916/2917/2920 record carries at @9, which decides its
/// loyalty level (@8, and the 2918 byte) against PetInfo.LoyaltyLevel_1.</summary>
public sealed class PetLoyalty
{
 /// <summary>PetInfo.NPC_ID of the pet.</summary>
    public uint PetId { get; set; }

 /// <summary>Loyalty, 0..PetInfo.LoyaltyLevel_1 (500 at most in the shipped table). Stored as an int, the
 /// BSON integer the driver writes for it.</summary>
    public int Loyalty { get; set; }
}
