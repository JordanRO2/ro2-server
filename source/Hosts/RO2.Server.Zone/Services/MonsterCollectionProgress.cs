using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;

using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Error-tolerant access to the monster collection progress repository, used by collection handlers. Storage errors are
/// logged and not rethrown, so a database failure does not drop the client's response.
/// </summary>
public sealed class MonsterCollectionProgressStore
{
    private readonly IMonsterCollectionProgressRepository _repo;
    private readonly ILogger _log;

    private MonsterCollectionProgressStore(IMonsterCollectionProgressRepository repo, ILogger log)
    {
        _repo = repo;
        _log = log;
    }

 /// <summary>Creates a store for a handler's service scope and logger.</summary>
    public static MonsterCollectionProgressStore From(IServiceProvider services, ILogger log) =>
        new(services.GetRequiredService<IMonsterCollectionProgressRepository>(), log);

 /// <summary>Loads the character's progress. Returns an empty document when none is stored or on a storage error.</summary>
    public async Task<MonsterCollectionProgressDocument> GetAsync(uint characterId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _repo.GetAsync(characterId, ct);
            return doc ?? new MonsterCollectionProgressDocument { Id = characterId };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MonsterCollection: progress load failed for char {Char}; using level 1 / exp 0", characterId);
            return new MonsterCollectionProgressDocument { Id = characterId };
        }
    }

 /// <summary>Saves the whole progress document.</summary>
    public async Task SaveAsync(MonsterCollectionProgressDocument doc, CancellationToken ct = default)
    {
        try
        {
            await _repo.SaveAsync(doc, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MonsterCollection: progress save failed for char {Char}", doc.Id);
        }
    }
}

/// <summary>Lookups that convert stored collection progress into the values the collection records carry.</summary>
public static class MonsterCollectionRules
{
 /// <summary>Result code when the collection is already at its highest level.</summary>
    public const ushort ResultMaxLevel = 30005;

 /// <summary>Result code when a monster's DNA registers a new collection.</summary>
    public const ushort ResultRegistered = 30008;

 /// <summary>Result code when a DNA adds experience to a registered collection.</summary>
    public const ushort ResultExpGained = 30009;

 /// <summary>Returns the CollectMonster row for a collection and level, or null.</summary>
    public static CollectMonster? Row(GameDataStore store, uint npcId, byte level)
    {
        foreach (var r in store.CollectMonster)
            if (r.NpcID == npcId && r.SummonLevel == level) return r;
        return null;
    }

 /// <summary>Highest summon level defined for a collection, or 0 when the id is not a collection.</summary>
    public static byte MaxLevel(GameDataStore store, uint npcId)
    {
        byte max = 0;
        foreach (var r in store.CollectMonster)
            if (r.NpcID == npcId && r.SummonLevel > max) max = r.SummonLevel;
        return max;
    }

 /// <summary>
 /// Wire state of a collection at its current level. The skill id is derived from the level, not stored.
 /// </summary>
    public static MonsterCollectionState State(GameDataStore store, uint npcId, byte level, uint exp) =>
        new(npcId, Row(store, npcId, level)?.SkillID ?? 0, level, exp);

 /// <summary>
 /// Wire state of one collection. A registered collection carries its stored level and experience. An unregistered
 /// collection is sent as level 0 with the level-1 skill id.
 /// </summary>
 /// <param name="registered">Whether the collection is registered. Required, so the caller chooses the form explicitly.</param>
    public static MonsterCollectionState State(GameDataStore store, MonsterCollectionProgressDocument progress,
        uint npcId, bool registered)
    {
        if (!registered) return new(npcId, Row(store, npcId, 1)?.SkillID ?? 0, 0, 0);
        var e = progress.Find(npcId);
        return State(store, npcId, e?.Level ?? 1, e?.Exp ?? 0);
    }

 /// <summary>
 /// Book records for the registered collections, ordered by collection id. Each record carries the stored level and
 /// experience and the skill id of that level.
 /// </summary>
    public static List<MonsterCollectionState> BookStates(GameDataStore store, IEnumerable<uint> registeredIds,
        MonsterCollectionProgressDocument progress) =>
        registeredIds.Where(id => id != 0).Distinct().OrderBy(id => id)
            .Select(id => State(store, progress, id, registered: true))
            .ToList();

 /// <summary>
 /// Count bucket of a registered-count header: the CollectRate row whose range contains <paramref name="count"/>, giving
 /// the index, maximum skill and skill cooldown.
 /// </summary>
    public static CollectRateHeader RateFor(GameDataStore store, int count)
    {
        CollectRate? best = null;
        foreach (var r in store.CollectRate)
        {
            if (count >= r.Num_Start && count <= r.Num_End) { best = r; break; }
            if (best is null || r.Num_End > best.Num_End) best = r; 
        }
        return best is null ? default : new CollectRateHeader(best.Index, best.Skill_Max, best.Skill_Cooltime);
    }

 /// <summary>
 /// Bag counts of the collection's four DNA pieces (CollectMonster.ItemID_Piece1 to Piece4), in the record's four u16 fields.
 /// </summary>
    public static Task<PieceCounts> MonsterPieceCountsAsync(GameDataStore store, IInventoryRepository inventory,
        uint characterId, uint npcId)
    {
        var row = Row(store, npcId, 1);
        return row is null
            ? Task.FromResult(default(PieceCounts))
            : CountAsync(inventory, characterId, row.ItemID_Piece1, row.ItemID_Piece2, row.ItemID_Piece3, row.ItemID_Piece4);
    }

 /// <summary>Returns the PetInfo row for a pet collection id, or null.</summary>
    public static PetInfo? Pet(GameDataStore store, uint petId)
    {
        foreach (var p in store.PetInfo)
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

 /// <summary>Bag counts of a pet's four PetInfo piece items.</summary>
    public static Task<PieceCounts> PetPieceCountsAsync(PetInfo pet, IInventoryRepository inventory, uint characterId) =>
        CountAsync(inventory, characterId, pet.ItemID_Piece1, pet.ItemID_Piece2, pet.ItemID_Piece3, pet.ItemID_Piece4);

 /// <summary>
 /// Builds the pet collection record from server state at the pet's stored loyalty. A pet with no loyalty or no PetInfo row
 /// uses the loyalty-0 form, and a pet id that PetInfo does not know keeps only its id.
 /// </summary>
    public static async Task<(PetCollectionState State, PieceCounts Pieces)> PetRecordAsync(
        GameDataStore store, IInventoryRepository inventory, uint characterId, uint petId, ushort loyalty)
    {
        var pet = Pet(store, petId);
        if (pet is null) return (new PetCollectionState(petId, 0, 0, 0), default);
        return (PetState(pet, loyalty), await PetPieceCountsAsync(pet, inventory, characterId));
    }

    private static async Task<PieceCounts> CountAsync(IInventoryRepository inventory, uint characterId,
        uint p1, uint p2, uint p3, uint p4)
    {
        var items = await inventory.GetByCharacterAsync(characterId);
        ushort Count(uint itemId)
        {
            if (itemId == 0) return 0;
            long n = 0;
            foreach (var it in items)
                if (it.StorageType == 0 && it.ItemId == itemId) n += it.Count;
            return (ushort)Math.Min(n, ushort.MaxValue);
        }
        return new PieceCounts(Count(p1), Count(p2), Count(p3), Count(p4));
    }
}
