using RO2.Application;
using RO2.Application.Interfaces;
using RO2.BuildingBlocks;
using RO2.Domain.Entities;
using RO2.Economy.Application;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IInventoryEscrow"/> over the inventory repository and the per-character
/// inventory gate. It holds the exact take/restore the auction listing used inline: the read-decide-write runs
/// under the seller's gate and the writes are owner-filtered and atomic, so a stack a concurrent trade / sale /
/// mail attach took first is taken by nobody. It maps between the game's inventory row and the neutral
/// <see cref="ItemStackSnapshot"/> the economy flows carry.
/// </summary>
public sealed class InventoryEscrowAdapter(IInventoryRepository inventory) : IInventoryEscrow
{
    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

    private readonly IInventoryRepository _inventory = inventory;

    /// <inheritdoc />
    public async Task<(ItemStackSnapshot? Taken, int CountLeft)> TakeAsync(uint characterId, ulong itemUid, int count)
    {
        return await CharacterInventoryGate.RunAsync(characterId, async () =>
        {
            var live = await _inventory.GetByIdAsync(itemUid);
            if (live == null || live.CharacterId != characterId || live.StorageType != BagStorageType || live.Count < count)
                return ((ItemStackSnapshot?)null, 0);
            if (count >= live.Count)
                return await _inventory.RemoveItemAsync(live.Id, characterId)
                    ? (ToSnapshot(live), 0)
                    : ((ItemStackSnapshot?)null, 0);
            var left = await _inventory.AdjustCountAsync(live.Id, characterId, -count);
            return left.HasValue ? (ToSnapshot(live), left.Value) : ((ItemStackSnapshot?)null, 0);
        });
    }

    /// <inheritdoc />
    public async Task RestoreAsync(uint characterId, ItemStackSnapshot taken, int countLeft, int count)
    {
        if (countLeft <= 0)
        {
            await _inventory.AddItemAsync(ToInventoryItem(taken, characterId)); // same Id and slot as before
            return;
        }
        if (await _inventory.AdjustCountAsync(taken.Id, characterId, count) is null)
        {
            // The rest of the stack left the bag too: re-add the taken part as a row of its own.
            var row = ToInventoryItem(taken, characterId);
            row.Id = 0;
            row.Slot = -1;
            row.Count = count;
            await _inventory.AddItemAsync(row);
        }
    }

    /// <inheritdoc />
    public Task<ulong> NextItemUidAsync() => _inventory.GetNextIdAsync();

    /// <summary>Captures an inventory row's full per-instance state as a neutral snapshot.</summary>
    internal static ItemStackSnapshot ToSnapshot(InventoryItem i) => new()
    {
        Id = i.Id,
        ItemId = i.ItemId,
        Count = i.Count,
        Slot = i.Slot,
        Refine = i.Refine,
        Element = i.Element,
        Durability = i.Durability,
        DyeColor = i.DyeColor,
        DyeColor2 = i.DyeColor2,
        DyeColor3 = i.DyeColor3,
        FxEnchantIndex = i.FxEnchantIndex,
        EnchantReinforceId = i.EnchantReinforceId,
        EnchantSeconds = i.EnchantSeconds,
        CardsJson = i.CardsJson,
        SocketPunchersJson = i.SocketPunchersJson,
        Bound = i.Bound,
        AbilityValue = i.AbilityValue,
        CreatedAt = i.CreatedAt,
    };

    /// <summary>Rebuilds a bag inventory row from a snapshot (owner set from <paramref name="characterId"/>).</summary>
    internal static InventoryItem ToInventoryItem(ItemStackSnapshot s, uint characterId) => new()
    {
        Id = s.Id,
        CharacterId = characterId,
        ItemId = s.ItemId,
        Count = s.Count,
        Slot = s.Slot,
        StorageType = BagStorageType,
        Refine = s.Refine,
        Element = s.Element,
        Durability = s.Durability,
        DyeColor = s.DyeColor,
        DyeColor2 = s.DyeColor2,
        DyeColor3 = s.DyeColor3,
        FxEnchantIndex = s.FxEnchantIndex,
        EnchantReinforceId = s.EnchantReinforceId,
        EnchantSeconds = s.EnchantSeconds,
        CardsJson = s.CardsJson,
        SocketPunchersJson = s.SocketPunchersJson,
        Bound = s.Bound,
        AbilityValue = s.AbilityValue,
        CreatedAt = s.CreatedAt,
    };
}
