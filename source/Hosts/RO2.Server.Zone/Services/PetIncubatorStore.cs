using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Zone-side half of the pet incubators: placing an incubator (1630), putting an egg in (1631) and collecting a
/// hatched egg (1632). Every answer is sent as 2926 (AnsPCollectionIncubator).
/// </summary>
public static class PetIncubatorStore
{
 /// <summary>Use source for items consumed by the pet collection (2329 OnUseItem).</summary>
    public const byte UseSourceCollection = 3;

    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> Locks = new();

 /// <summary>
 /// Serializes incubator requests of one character, so two quick requests cannot act on the same state
 /// (for example, granting two eggs from one collect).
 /// </summary>
    public static async Task<IDisposable> LockAsync(uint characterId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }

    
 /// <summary>Returns the character's incubator slots, or null when the storage could not be read.</summary>
    public static Task<IReadOnlyList<PetIncubatorSlot>?> LoadAsync(IPetIncubatorRepository repo, uint characterId,
        ILogger log, CancellationToken ct)
        => GuardAsync<IReadOnlyList<PetIncubatorSlot>?>(async () => await repo.GetSlotsAsync(characterId, ct),
            "load", characterId, log, null);

 /// <summary>Places an incubator in a slot. Returns false when the placement was refused or the write failed.</summary>
    public static Task<bool> TryPlaceAsync(IPetIncubatorRepository repo, uint characterId, byte slot, uint incubatorId,
        byte charges, DateTime nowUtc, ILogger log, CancellationToken ct)
        => GuardAsync(async () => await repo.TryPlaceIncubatorAsync(characterId, slot, incubatorId, charges, nowUtc, ct),
            "update", characterId, log, false);

 /// <summary>Puts an egg into an incubator slot and starts incubation. Returns false when refused or the write failed.</summary>
    public static Task<bool> TryStartAsync(IPetIncubatorRepository repo, uint characterId, byte slot, uint incubatorId,
        uint eggId, DateTime startUtc, DateTime endUtc, ILogger log, CancellationToken ct)
        => GuardAsync(async () => await repo.TryStartIncubationAsync(characterId, slot, incubatorId, eggId, startUtc, endUtc, ct),
            "update", characterId, log, false);

 /// <summary>Collects a hatched egg from a slot. Returns null when there is nothing to collect or the write failed.</summary>
    public static Task<PetIncubatorSlot?> TryCollectAsync(IPetIncubatorRepository repo, uint characterId, byte slot,
        uint incubatorId, DateTime nowUtc, ILogger log, CancellationToken ct)
        => GuardAsync<PetIncubatorSlot?>(async () => await repo.TryCollectAsync(characterId, slot, incubatorId, nowUtc, ct),
            "update", characterId, log, null);

 /// <summary>Restores a slot after a failed bag landing. A failure is logged and ignored.</summary>
    public static async Task RestoreAsync(IPetIncubatorRepository repo, uint characterId, PetIncubatorSlot before,
        ILogger log, CancellationToken ct)
        => await GuardAsync(async () => { await repo.RestoreSlotAsync(characterId, before, ct); return true; },
            "update", characterId, log, false);

    private static async Task<T> GuardAsync<T>(Func<Task<T>> op, string action, uint characterId, ILogger log, T onError)
    {
        try
        {
            return await op();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Incubators: {Action} failed for char {Char}", action, characterId);
            return onError;
        }
    }

    
 /// <summary>Returns the incubator definition of an item, or null.</summary>
    public static IncubatorInfo? Incubator(GameDataStore store, uint incubatorId)
    {
        foreach (var row in store.IncubatorInfo)
            if (row.Incubator_ID == incubatorId) return row;
        return null;
    }

 /// <summary>Returns the pet definition whose egg item is <paramref name="eggId"/>, or null.</summary>
    public static PetInfo? PetByEgg(GameDataStore store, uint eggId)
    {
        foreach (var row in store.PetInfo)
            if (eggId != 0 && row.Pet_Egg_ID == eggId) return row;
        return null;
    }

 /// <summary>Returns the incubation time of an egg in an incubator, in seconds.</summary>
    public static uint IncubationSeconds(IncubatorInfo incubator, PetInfo pet)
        => (uint)((ulong)incubator.Incubation_Speed * pet.Incubation_Time / 10000);

    
 /// <summary>Maps a client bag and slot to a flat bag slot index.</summary>
    public static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }

 /// <summary>Returns the bag item in the client cell (bag, slot) when it is a stack of <paramref name="itemId"/>, or null.</summary>
    public static async Task<InventoryItem?> BagItemAsync(IInventoryRepository inventory, uint characterId,
        byte bag, byte slot, uint itemId)
    {
        int flat = BagSlotToFlat(bag, slot);
        if (flat < 0) return null;
        var item = (await inventory.GetByCharacterAsync(characterId))
            .FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        return item != null && item.ItemId == itemId && item.Count > 0 ? item : null;
    }

 /// <summary>
 /// Takes one item from <paramref name="item"/>'s stack and sends 2329 (OnUseItem) for the cell with the count
 /// left. A count of 0 empties the cell.
 /// </summary>
    public static async Task ConsumeOneAsync(ProudSession session, PlayerState player, IInventoryRepository inventory,
        InventoryItem item, byte bag, byte slot, CancellationToken ct)
    {
        int remaining = item.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(item.Id); remaining = 0; }
        else { item.Count = remaining; await inventory.UpdateItemAsync(item); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, CombatPackets.BuildUseItem2329(player.EntityId, slot, bag,
            item.Id, (byte)Math.Clamp(remaining, 0, 255), UseSourceCollection), ct);
    }

    
 /// <summary>
 /// Sends 2926 with <paramref name="slots"/>. A list that could not be loaded (null) is sent as the empty list.
 /// </summary>
    public static Task SendListAsync(ProudSession session, IReadOnlyList<PetIncubatorSlot>? slots, CancellationToken ct)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionIncubator,
            PetIncubatorPackets.BuildAnsPCollectionIncubator(slots ?? [], DateTime.UtcNow), ct);
}
