using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Inventory.Domain;
using RO2.Persistence.Mongo.Persistence;

using RO2.Application;

namespace RO2.Persistence.Mongo.Repositories;

public class InventoryRepository : IInventoryRepository
{
    /// <summary>The <c>_id</c> of the item-id sequence document in <see cref="CounterCollectionName"/>.</summary>
    public const string CounterId = "inventoryItemId";
    public const string CounterCollectionName = "inventory_counters";

    /// <summary>Name of the unique placement index (see <see cref="EnsureIndexesAsync"/>).</summary>
    public const string SlotIndexName = "uq_inventory_char_storage_slot";

    private const string ItemsCollectionName = "inventory_items";

    private const int MaxSlotAssignAttempts = 5;

    // InventoryItem.StorageType value 1 marks a worn row, whose Slot is its equipment position (never reassigned);
    // every other storage (bag 0, bank 2, ...) is placed by a flat Slot index.

    /// <summary>StorageType of a worn equipment slot.</summary>
    private const byte StorageWorn = 1;

    /// <summary>Mongo duplicate-key error code (E11000), raised when existing rows violate the unique placement index.</summary>
    private const int DuplicateKeyErrorCode = 11000;

    /// <summary>Field of the sequence document that holds the last reserved item id.</summary>
    private const string CounterSeqField = "seq";

    private readonly IMongoCollection<InventoryItem> _items;
    private readonly IMongoCollection<BsonDocument> _counters;

    // The counter is seeded from the existing rows once per process (the seed is idempotent, so several
    // processes seeding at once is harmless).
    private static int s_counterSeeded;

    public InventoryRepository(MongoDbContext context)
    {
        _items = context.GetCollection<InventoryItem>(ItemsCollectionName);
        _counters = context.GetCollection<BsonDocument>(CounterCollectionName);
        // No index creation here: a synchronous CreateMany in the constructor stalls the whole boot ~30 s
        // when Mongo is down (the MailRepository/AuctionHouse lesson). Indexes: EnsureIndexesAsync.
    }

    public async Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId)
    {
        return await _items.Find(i => i.CharacterId == characterId)
            .SortBy(i => i.Slot)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId)
    {
        return await _items.Find(i => i.CharacterId == characterId && i.StorageType == StorageWorn)
            .ToListAsync();
    }

    public async Task<InventoryItem?> GetByIdAsync(ulong id)
    {
        return await _items.Find(i => i.Id == id).FirstOrDefaultAsync();
    }

    public async Task AddItemAsync(InventoryItem item)
    {
        if (item.Id == 0)
            item.Id = await GetNextIdAsync();
        if (item.Slot >= 0)
        {
            await _items.InsertOneAsync(item);
            return;
        }

        // Assign the lowest free slot for this character's storage of this type. The client places
        // an item purely by the (bag, slot) carried in its record, so a real slot index is required
        // — a slot of -1/0-for-everything means the item never shows in the bag.
        // The pick-then-insert runs under the character's gate so two concurrent adds in this process cannot
        // pick the same free slot; if another process wins the slot, the unique index (when ensured)
        // rejects the insert and the pick is retried.
        await CharacterInventoryGate.RunAsync(item.CharacterId, async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var used = await _items
                    .Find(i => i.CharacterId == item.CharacterId && i.StorageType == item.StorageType)
                    .Project(i => (int)i.Slot).ToListAsync();
                item.Slot = LowestFreeSlot(used);
                try
                {
                    await _items.InsertOneAsync(item);
                    return 0;
                }
                catch (MongoWriteException ex) when (IsSlotCollision(ex) && attempt < MaxSlotAssignAttempts)
                {
                    // lost the slot to another process: re-read and pick again
                }
            }
        });
    }

    /// <summary>The lowest slot index (from 0) that is not in <paramref name="occupied"/>.</summary>
    public static short LowestFreeSlot(IEnumerable<int> occupied) => BagLayout.LowestFreeSlot(occupied);

    private static bool IsSlotCollision(MongoWriteException ex) =>
        ex.WriteError.Category == ServerErrorCategory.DuplicateKey
        && ex.WriteError.Message.Contains(SlotIndexName, StringComparison.Ordinal);

    public async Task RemoveItemAsync(ulong itemId)
    {
        await _items.DeleteOneAsync(i => i.Id == itemId);
    }

    /// <summary>
    /// Deletes the row only if it belongs to <paramref name="characterId"/>, so a forged or stale item id can never
    /// remove another character's row. True when a row was deleted.
    /// </summary>
    public async Task<bool> RemoveItemAsync(ulong itemId, uint characterId)
    {
        var result = await _items.DeleteOneAsync(BuildOwnedFilter(itemId, characterId));
        return result.DeletedCount > 0;
    }

    /// <summary>
    /// Writes the item's fields with a field-level <c>$set</c> (see <see cref="BuildItemUpdate"/>) instead of
    /// replacing the whole document, so a field this model does not carry is not wiped. The repository cannot know
    /// which fields the caller changed, so it sets every mutable one; use <see cref="AdjustCountAsync"/> for a
    /// count change. Filtered by id only: a trade may legitimately change <c>CharacterId</c> on the row.
    /// </summary>
    public async Task UpdateItemAsync(InventoryItem item)
    {
        await _items.UpdateOneAsync(i => i.Id == item.Id, BuildItemUpdate(item));
    }

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to the stack's <c>Count</c> with <c>$inc</c>, guarded in the same
    /// operation: the row must belong to <paramref name="characterId"/> and a negative delta must not take the
    /// count below 0. Returns the new count, or null when no row matched (not found, not the owner, or not enough
    /// in the stack), in which case nothing changed.
    /// </summary>
    public async Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta)
    {
        var options = new FindOneAndUpdateOptions<InventoryItem>
        {
            ReturnDocument = ReturnDocument.After,
            Projection = Builders<InventoryItem>.Projection.Include(i => i.Count),
        };
        var after = await _items.FindOneAndUpdateAsync(
            BuildAdjustCountFilter(itemId, characterId, delta), BuildAdjustCountUpdate(delta), options);
        return after?.Count;
    }

    /// <summary>
    /// Re-homes the row with one <c>findOneAndUpdate</c> filtered on id AND the sender, so a stale or forged owner
    /// matches nothing and a concurrent move of the same row has exactly one winner. True when a row matched. A
    /// destination slot already taken trips the unique placement index (when ensured) and the write throws.
    /// </summary>
    public async Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
    {
        var options = new FindOneAndUpdateOptions<InventoryItem>
        {
            ReturnDocument = ReturnDocument.After,
            Projection = Builders<InventoryItem>.Projection.Include(i => i.Id),
        };
        var after = await _items.FindOneAndUpdateAsync(
            BuildOwnedFilter(itemId, fromCharId), BuildReassignUpdate(toCharId, toStorageType, toSlot), options);
        return after is not null;
    }

    public static UpdateDefinition<InventoryItem> BuildReassignUpdate(uint toCharId, byte toStorageType, short toSlot)
    {
        var u = Builders<InventoryItem>.Update;
        return u.Combine(
            u.Set(i => i.CharacterId, toCharId),
            u.Set(i => i.StorageType, toStorageType),
            u.Set(i => i.Slot, toSlot));
    }

    /// <summary>The filter that selects a row by id AND owner.</summary>
    public static FilterDefinition<InventoryItem> BuildOwnedFilter(ulong itemId, uint characterId) =>
        Builders<InventoryItem>.Filter.And(
            Builders<InventoryItem>.Filter.Eq(i => i.Id, itemId),
            Builders<InventoryItem>.Filter.Eq(i => i.CharacterId, characterId));

    /// <summary>Owned row, and (for a negative delta) a stack large enough to lose <c>-delta</c>.</summary>
    public static FilterDefinition<InventoryItem> BuildAdjustCountFilter(ulong itemId, uint characterId, int delta)
    {
        var owned = BuildOwnedFilter(itemId, characterId);
        return delta < 0
            ? Builders<InventoryItem>.Filter.And(owned, Builders<InventoryItem>.Filter.Gte(i => i.Count, -delta))
            : owned;
    }

    public static UpdateDefinition<InventoryItem> BuildAdjustCountUpdate(int delta) =>
        Builders<InventoryItem>.Update.Inc(i => i.Count, delta);

    /// <summary>
    /// A <c>$set</c> of every mutable field of the row: all of them except the immutable <c>Id</c>, the creation
    /// stamp and the navigation property.
    /// </summary>
    public static UpdateDefinition<InventoryItem> BuildItemUpdate(InventoryItem item)
    {
        var u = Builders<InventoryItem>.Update;
        return u.Combine(
            u.Set(i => i.CharacterId, item.CharacterId),
            u.Set(i => i.ItemId, item.ItemId),
            u.Set(i => i.Count, item.Count),
            u.Set(i => i.Slot, item.Slot),
            u.Set(i => i.StorageType, item.StorageType),
            u.Set(i => i.Refine, item.Refine),
            u.Set(i => i.Element, item.Element),
            u.Set(i => i.Durability, item.Durability),
            u.Set(i => i.DyeColor, item.DyeColor),
            u.Set(i => i.DyeColor2, item.DyeColor2),
            u.Set(i => i.DyeColor3, item.DyeColor3),
            u.Set(i => i.FxEnchantIndex, item.FxEnchantIndex),
            u.Set(i => i.EnchantReinforceId, item.EnchantReinforceId),
            u.Set(i => i.EnchantSeconds, item.EnchantSeconds),
            u.Set(i => i.CardsJson, item.CardsJson),
            u.Set(i => i.SocketPunchersJson, item.SocketPunchersJson),
            u.Set(i => i.Bound, item.Bound),
            u.Set(i => i.AbilityValue, item.AbilityValue));
    }

    /// <summary>
    /// Reserves and returns a fresh item id from the atomic <c>inventory_counters</c> sequence
    /// (<c>findOneAndUpdate $inc</c>), so two concurrent callers always get distinct ids. It reserves: the number
    /// is consumed even if the caller never inserts a row with it (gaps are harmless). The first call in a
    /// process seeds the sequence from the highest existing row id, so ids keep growing past every row that
    /// exists today.
    /// </summary>
    public async Task<ulong> GetNextIdAsync()
    {
        await EnsureCounterSeededAsync();
        var doc = await _counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", CounterId),
            BuildCounterIncrement(),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
        return (ulong)doc[CounterSeqField].ToInt64();
    }

    public static UpdateDefinition<BsonDocument> BuildCounterIncrement() =>
        Builders<BsonDocument>.Update.Inc(CounterSeqField, 1L);

    public static UpdateDefinition<BsonDocument> BuildCounterSeed(ulong highestExistingId) =>
        Builders<BsonDocument>.Update.SetOnInsert(CounterSeqField, checked((long)highestExistingId));

    private async Task EnsureCounterSeededAsync()
    {
        if (Volatile.Read(ref s_counterSeeded) == 1) return;
        var highest = await _items.Find(_ => true)
            .SortByDescending(i => i.Id)
            .Limit(1)
            .Project(i => i.Id)
            .FirstOrDefaultAsync();
        try
        {
            // $setOnInsert: an existing counter (even one already advanced past the rows) is never lowered.
            await _counters.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", CounterId),
                BuildCounterSeed(highest),
                new UpdateOptions { IsUpsert = true });
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // another caller created the counter between our upsert's match and insert: it is seeded.
        }
        Volatile.Write(ref s_counterSeeded, 1);
    }

    /// <summary>
    /// Creates the unique placement index on <c>(CharacterId, StorageType, Slot)</c> for rows with a real slot
    /// (<c>Slot &gt;= 0</c>; unplaced Slot -1 rows are exempt). Idempotent. Returns false (and creates nothing)
    /// when existing rows already collide, in which case run <see cref="RepairSlotsAsync"/> per affected
    /// character first. Deliberately NOT called from the constructor.
    /// </summary>
    public async Task<bool> EnsureIndexesAsync()
    {
        var keys = Builders<InventoryItem>.IndexKeys
            .Ascending(i => i.CharacterId).Ascending(i => i.StorageType).Ascending(i => i.Slot);
        var model = new CreateIndexModel<InventoryItem>(keys, new CreateIndexOptions<InventoryItem>
        {
            Name = SlotIndexName,
            Unique = true,
            PartialFilterExpression = Builders<InventoryItem>.Filter.Gte(i => i.Slot, (short)0),
        });
        try
        {
            await _items.Indexes.CreateOneAsync(model);
            return true;
        }
        catch (MongoCommandException ex) when (ex.Code == DuplicateKeyErrorCode)
        {
            return false;
        }
    }

    public async Task<int> RepairSlotsAsync(uint characterId)
    {
        var items = await _items.Find(i => i.CharacterId == characterId).ToListAsync();
        var repaired = 0;
        // Per storage type: reassign every item that has no slot (Slot<0) OR that COLLIDES with a slot another
        // item already claimed, to the lowest free index. Processing by ascending slot (unplaced last) and then
        // by id makes the first claimant of a slot keep it, and each duplicate move to a free cell — so bags
        // stacked on one cell (all at the same Slot) spread out. Equipment (StorageType 1) keeps its body-part
        // slot untouched.
        foreach (var grp in items.GroupBy(i => i.StorageType))
        {
            if (grp.Key == StorageWorn) continue; // equipment slot = equip position, never reassign
            foreach (var move in BagLayout.PlanSlotRepair(grp.Select(i => (i.Id, i.Slot))))
            {
                await _items.UpdateOneAsync(x => x.Id == move.Id,
                    Builders<InventoryItem>.Update.Set(x => x.Slot, move.Slot));
                repaired++;
            }
        }
        return repaired;
    }
}
