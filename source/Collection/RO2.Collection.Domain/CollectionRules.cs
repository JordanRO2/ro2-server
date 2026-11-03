using RO2.GameData.Tables;

namespace RO2.Collections.Domain;

/// <summary>
/// The pure monster and pet collection rules: collect-rate bucket, summon-level lookups, the wire states of a collection
/// and the pet loyalty feed. The GameData rows come in as arguments; nothing here reads a store, the bag or the wire.
/// </summary>
public static class CollectionRules
{
    /// <summary>Loyalty gained by feeding one egg to a pet already in the collection.</summary>
    public const ushort LoyaltyPerFeed = 10;

    /// <summary>Returns the CollectMonster row for a collection and level, or null.</summary>
    public static CollectMonster? Row(IEnumerable<CollectMonster> rows, uint npcId, byte level)
    {
        foreach (var r in rows)
            if (r.NpcID == npcId && r.SummonLevel == level) return r;
        return null;
    }

    /// <summary>Highest summon level defined for a collection, or 0 when the id is not a collection.</summary>
    public static byte MaxLevel(IEnumerable<CollectMonster> rows, uint npcId)
    {
        byte max = 0;
        foreach (var r in rows)
            if (r.NpcID == npcId && r.SummonLevel > max) max = r.SummonLevel;
        return max;
    }

    /// <summary>Wire state of a collection at its current level. The skill id is derived from the level, not stored.</summary>
    public static MonsterCollectionState State(IEnumerable<CollectMonster> rows, uint npcId, byte level, uint exp) =>
        new(npcId, Row(rows, npcId, level)?.SkillID ?? 0, level, exp);

    /// <summary>
    /// Wire state of one collection. A registered collection carries its stored level and experience. An unregistered
    /// collection is sent as level 0 with the level-1 skill id.
    /// </summary>
    public static MonsterCollectionState State(IEnumerable<CollectMonster> rows, MonsterCollectionProgressDocument progress,
        uint npcId, bool registered)
    {
        if (!registered) return new(npcId, Row(rows, npcId, 1)?.SkillID ?? 0, 0, 0);
        var e = progress.Find(npcId);
        return State(rows, npcId, e?.Level ?? 1, e?.Exp ?? 0);
    }

    /// <summary>Book records for the registered collections, ordered by collection id.</summary>
    public static List<MonsterCollectionState> BookStates(IEnumerable<CollectMonster> rows, IEnumerable<uint> registeredIds,
        MonsterCollectionProgressDocument progress) =>
        registeredIds.Where(id => id != 0).Distinct().OrderBy(id => id)
            .Select(id => State(rows, progress, id, registered: true))
            .ToList();

    /// <summary>
    /// Count bucket of a registered-count header: the CollectRate row whose range contains <paramref name="count"/>.
    /// Below every range it falls back to the highest-ending tier, and in a gap to the highest tier seen so far.
    /// </summary>
    public static CollectRateHeader RateFor(IEnumerable<CollectRate> rows, int count)
    {
        CollectRate? best = null;
        foreach (var r in rows)
        {
            if (count >= r.Num_Start && count <= r.Num_End) { best = r; break; }
            if (best is null || r.Num_End > best.Num_End) best = r;
        }
        return best is null ? default : new CollectRateHeader(best.Index, best.Skill_Max, best.Skill_Cooltime);
    }

    /// <summary>Returns the PetInfo row for a pet collection id, or null.</summary>
    public static PetInfo? Pet(IEnumerable<PetInfo> rows, uint petId)
    {
        foreach (var p in rows)
            if (p.NPC_ID == petId) return p;
        return null;
    }

    /// <summary>
    /// Wire state of a pet record at a given loyalty. The effect is that of the highest loyalty level reached, and the level
    /// index and loyalty value are also included.
    /// </summary>
    public static PetCollectionState PetState(PetInfo pet, ushort loyalty) =>
        loyalty >= pet.LoyaltyLevel_1
            ? new PetCollectionState(pet.NPC_ID, pet.LoyaltyEffect_1, 1, loyalty)
            : new PetCollectionState(pet.NPC_ID, pet.LoyaltyEffect_0, 0, loyalty);

    /// <summary>
    /// Collection record fields of a pet at the given loyalty. No pet data, or a pet id the data does not know, keeps
    /// only the id.
    /// </summary>
    public static PetCollectionState PetState(IEnumerable<PetInfo>? rows, uint petId, ushort loyalty) =>
        rows is not null && Pet(rows, petId) is { } pet
            ? PetState(pet, loyalty)
            : new PetCollectionState(petId, 0, 0, 0);

    /// <summary>The book (2916): one record per registered pet, in ascending pet ID order, each at its stored loyalty.</summary>
    public static List<PetCollectionState> PetBookStates(IEnumerable<PetInfo>? rows, CollectionDocument book) =>
        book.RegisteredPetIds.Where(id => id != 0).Distinct().OrderBy(id => id)
            .Select(id => PetState(rows, id, book.LoyaltyOf(id)))
            .ToList();

    /// <summary>The active-pet flag (2918): the active pet's loyalty level.</summary>
    public static byte PetActiveFlag(IEnumerable<PetInfo>? rows, uint petId, ushort loyalty) =>
        PetState(rows, petId, loyalty).LoyaltyLevel;

    /// <summary>The loyalty after feeding a pet one egg, or null when the pet is already at its loyalty level cap.</summary>
    public static ushort? FedLoyalty(PetInfo pet, ushort loyalty)
    {
        var cap = pet.LoyaltyLevel_1;
        if (loyalty >= cap)
            return null;
        return (ushort)Math.Min(loyalty + LoyaltyPerFeed, cap);
    }
}
