using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Inventory.Domain;

namespace RO2.Tests.Economy;

/// <summary>
/// In-memory <see cref="IInventoryRepository"/> for <see cref="EconomyFixture"/>. Mirrors the Mongo repository's
/// observable behaviour: owner-filtered atomic methods (<see cref="AdjustCountAsync"/>, <see cref="TryReassignAsync"/>,
/// the owner overload of <see cref="RemoveItemAsync(ulong, uint)"/>) are single atomic steps after one scheduler point,
/// while the non-atomic pair read (<see cref="GetByIdAsync"/>) / write (<see cref="UpdateItemAsync"/>) are two points.
/// Rows are copied in and out. The unique (character, storage, slot) index applies once
/// <see cref="EnsureIndexesAsync"/> has been called on it, for slots &gt;= 0 (the real partial index).
/// </summary>
public sealed class FakeInventoryRepository(RepoScheduler scheduler) : IInventoryRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, InventoryItem> _rows = [];
    private long _nextId;

    /// <summary>True after <see cref="EnsureIndexesAsync"/> succeeded: a write that would put two rows on one
    /// (character, storage type, slot >= 0) throws, as the Mongo unique index does.</summary>
    public bool UniquePlacementEnforced { get; private set; }

    /// <summary>Copies of every stored row (for invariants and assertions); takes no scheduler point.</summary>
    public IReadOnlyList<InventoryItem> Rows { get { lock (_gate) return [.. _rows.Values.Select(EntityCopy.Clone)]; } }

    /// <summary>Test setup: stores a copy of the row as is (assigning an id when it has none) without a scheduler
    /// point or index check, and returns the stored id.</summary>
    public ulong Seed(InventoryItem item)
    {
        lock (_gate)
        {
            var row = EntityCopy.Clone(item);
            if (row.Id == 0) row.Id = (ulong)Interlocked.Increment(ref _nextId);
            else if (row.Id > (ulong)Interlocked.Read(ref _nextId)) Interlocked.Exchange(ref _nextId, (long)row.Id);
            _rows[row.Id] = row;
            return row.Id;
        }
    }

    public async Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId)
    {
        await scheduler.PointAsync("Inventory.GetByCharacter");
        lock (_gate)
            return [.. _rows.Values.Where(i => i.CharacterId == characterId).OrderBy(i => i.Slot).Select(EntityCopy.Clone)];
    }

    public async Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId)
    {
        await scheduler.PointAsync("Inventory.GetEquipment");
        lock (_gate)
            return [.. _rows.Values.Where(i => i.CharacterId == characterId && i.StorageType == 1).Select(EntityCopy.Clone)];
    }

    public async Task<InventoryItem?> GetByIdAsync(ulong id)
    {
        await scheduler.PointAsync("Inventory.GetById");
        lock (_gate) return _rows.TryGetValue(id, out var row) ? EntityCopy.Clone(row) : null;
    }

    public async Task AddItemAsync(InventoryItem item)
    {
        if (item.Id == 0) item.Id = await GetNextIdAsync();
        await scheduler.PointAsync("Inventory.Add");
        lock (_gate)
        {
            if (_rows.ContainsKey(item.Id)) throw new InvalidOperationException($"duplicate inventory row id {item.Id}");
            var row = EntityCopy.Clone(item);
            if (row.Slot < 0)
            {
                // The real repository assigns the lowest free slot of this character's storage type.
                var used = _rows.Values.Where(i => i.CharacterId == row.CharacterId && i.StorageType == row.StorageType)
                    .Select(i => (int)i.Slot).ToHashSet();
                short slot = 0;
                while (used.Contains(slot)) slot++;
                row.Slot = slot;
                item.Slot = slot;
            }
            CheckPlacement(row);
            _rows[row.Id] = row;
        }
    }

    public async Task RemoveItemAsync(ulong itemId)
    {
        await scheduler.PointAsync("Inventory.Remove");
        lock (_gate) _rows.Remove(itemId);
    }

    public async Task<bool> RemoveItemAsync(ulong itemId, uint characterId)
    {
        await scheduler.PointAsync("Inventory.RemoveOwned");
        lock (_gate)
        {
            if (!_rows.TryGetValue(itemId, out var row) || row.CharacterId != characterId) return false;
            _rows.Remove(itemId);
            return true;
        }
    }

    /// <summary>Whole-row write by id (the Mongo version <c>$set</c>s every mutable field); no row, no write.
    /// The id and creation stamp are kept.</summary>
    public async Task UpdateItemAsync(InventoryItem item)
    {
        await scheduler.PointAsync("Inventory.Update");
        lock (_gate)
        {
            if (!_rows.TryGetValue(item.Id, out var existing)) return;
            var row = EntityCopy.Clone(item);
            row.CreatedAt = existing.CreatedAt;
            CheckPlacement(row);
            _rows[row.Id] = row;
        }
    }

    public Task<ulong> GetNextIdAsync() => NextIdAsync();

    private async Task<ulong> NextIdAsync()
    {
        await scheduler.PointAsync("Inventory.NextId");
        return (ulong)Interlocked.Increment(ref _nextId);
    }

    public async Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta)
    {
        await scheduler.PointAsync("Inventory.AdjustCount");
        lock (_gate)
        {
            if (!_rows.TryGetValue(itemId, out var row) || row.CharacterId != characterId
                || (long)row.Count + delta < 0) return null;
            row.Count += delta;
            return row.Count;
        }
    }

    public async Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
    {
        await scheduler.PointAsync("Inventory.TryReassign");
        lock (_gate)
        {
            if (!_rows.TryGetValue(itemId, out var row) || row.CharacterId != fromCharId) return false;
            var moved = EntityCopy.Clone(row);
            moved.CharacterId = toCharId;
            moved.StorageType = toStorageType;
            moved.Slot = toSlot;
            CheckPlacement(moved);
            _rows[itemId] = moved;
            return true;
        }
    }

    public async Task<bool> EnsureIndexesAsync()
    {
        await scheduler.PointAsync("Inventory.EnsureIndexes");
        lock (_gate)
        {
            if (HasPlacementCollision()) return false;
            UniquePlacementEnforced = true;
            return true;
        }
    }

    public async Task<int> RepairSlotsAsync(uint characterId)
    {
        await scheduler.PointAsync("Inventory.RepairSlots");
        lock (_gate)
        {
            var repaired = 0;
            foreach (var row in _rows.Values.Where(i => i.CharacterId == characterId && i.StorageType != 1 && i.Slot < 0)
                         .OrderBy(i => i.Id).ToList())
            {
                var used = _rows.Values.Where(i => i.CharacterId == characterId && i.StorageType == row.StorageType)
                    .Select(i => (int)i.Slot).ToHashSet();
                short slot = 0;
                while (used.Contains(slot)) slot++;
                row.Slot = slot;
                repaired++;
            }
            return repaired;
        }
    }

    // Called under _gate. Throws when the row would share a (character, storage, slot >= 0) with a different row.
    private void CheckPlacement(InventoryItem row)
    {
        if (!UniquePlacementEnforced || row.Slot < 0) return;
        if (_rows.Values.Any(o => o.Id != row.Id && o.CharacterId == row.CharacterId
                                  && o.StorageType == row.StorageType && o.Slot == row.Slot))
            throw new InvalidOperationException(
                $"duplicate key: uq_inventory_char_storage_slot ({row.CharacterId}, {row.StorageType}, {row.Slot})");
    }

    private bool HasPlacementCollision() => _rows.Values.Where(r => r.Slot >= 0)
        .GroupBy(r => (r.CharacterId, r.StorageType, r.Slot)).Any(g => g.Count() > 1);
}
