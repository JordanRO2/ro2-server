using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

public interface IInventoryRepository
{
    Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId);
    Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId);
    Task<InventoryItem?> GetByIdAsync(ulong id);
    Task AddItemAsync(InventoryItem item);
    Task RemoveItemAsync(ulong itemId);
    Task UpdateItemAsync(InventoryItem item);
    Task<ulong> GetNextIdAsync();

    // SP0-INV. These three have default (non-atomic) bodies so existing implementations and test fakes keep
    // compiling; InventoryRepository overrides them with the atomic Mongo versions.

    /// <summary>Deletes the row only if it belongs to <paramref name="characterId"/>. True when a row was deleted.</summary>
    async Task<bool> RemoveItemAsync(ulong itemId, uint characterId)
    {
        var row = await GetByIdAsync(itemId);
        if (row is null || row.CharacterId != characterId) return false;
        await RemoveItemAsync(itemId);
        return true;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the owned stack's Count; a negative delta that would take it below 0
    /// changes nothing. Returns the new count, or null when the row is missing, not the owner's, or too small.
    /// </summary>
    async Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta)
    {
        var row = await GetByIdAsync(itemId);
        if (row is null || row.CharacterId != characterId || (long)row.Count + delta < 0) return null;
        row.Count += delta;
        await UpdateItemAsync(row);
        return row.Count;
    }

    /// <summary>
    /// Moves the row from <paramref name="fromCharId"/> to <paramref name="toCharId"/> at the given storage and slot in
    /// one guarded write (the row must still belong to <paramref name="fromCharId"/>). True when the row moved; false
    /// when it is missing or no longer the sender's. A slot already taken at the destination throws (the unique
    /// placement index), changing nothing. The default body is the non-atomic read-modify-write for test fakes.
    /// </summary>
    async Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
    {
        var row = await GetByIdAsync(itemId);
        if (row is null || row.CharacterId != fromCharId) return false;
        row.CharacterId = toCharId;
        row.StorageType = toStorageType;
        row.Slot = toSlot;
        await UpdateItemAsync(row);
        return true;
    }

    /// <summary>Creates the unique placement index; false when existing rows collide. No-op by default.</summary>
    Task<bool> EnsureIndexesAsync() => Task.FromResult(true);

    /// <summary>
    /// Give every bag/storage item that has an unassigned slot (Slot &lt; 0) a real, unique slot index, so
    /// the client can place it. Items created before slot-assignment existed (or via a path that skipped it)
    /// sit at Slot -1, and the wire mapping collapses every Slot &lt; 0 onto bag slot 0 — so they all pile
    /// onto one cell and the bag shows a different one each login. Equipment (StorageType 1) is left alone:
    /// its Slot is the body-part position, not a free cell. Returns the number of items repaired.
    /// </summary>
    Task<int> RepairSlotsAsync(uint characterId);
}
