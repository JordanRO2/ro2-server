using RO2.Guilds.Domain;
using RO2.Inventory.Domain;

namespace RO2.Guilds.Application;

/// <summary>Why a vault deposit did not move the item.</summary>
public enum VaultDepositRefusal
{
    /// <summary>The item was moved.</summary>
    None,

    /// <summary>The item is no longer an owned bag item, the guild was disbanded, or the move failed and was undone.</summary>
    NotMoved,

    /// <summary>No vault cell below the capacity is free.</summary>
    VaultFull,
}

/// <summary>
/// Sequences moving one item between a character's bag and the guild vault. Deposit: the room check first, then the
/// item is removed from the bag before it is added to the vault, so it is never in both places; a failed or refused
/// store restores the item to the bag. Withdraw: the record is removed from the vault and written before the item
/// is added to the bag, and a failed add puts the record back. The host keeps the per-character inventory gate, the
/// request checks, the wire sends and the storage log.
/// </summary>
public sealed class GuildVaultUseCase(IGuildVaultLedger vault)
{
    /// <summary>StorageType of a character's bag; only bag items can be deposited, and a withdrawn item returns to it.</summary>
    private const byte StorageBag = 0;

    /// <summary>Slot of an item not yet placed in a cell; the inventory store then assigns the lowest free bag cell.</summary>
    private const short UnplacedSlot = -1;

    /// <summary>Moves <paramref name="item"/> from its owner's bag to the lowest free vault cell below <paramref name="capacity"/>.</summary>
    public async Task<(GuildStorageItem? Stored, VaultDepositRefusal Refusal)> DepositAsync(
        Guild guild, InventoryItem item, IInventoryRepository inv, int capacity)
    {
        var live = await inv.GetByIdAsync(item.Id);
        if (live == null || live.CharacterId != item.CharacterId || live.StorageType != StorageBag)
            return (null, VaultDepositRefusal.NotMoved);

        switch (vault.CheckRoom(guild, capacity))
        {
            case VaultStoreOutcome.NotRegistered: return (null, VaultDepositRefusal.NotMoved);
            case VaultStoreOutcome.Full: return (null, VaultDepositRefusal.VaultFull);
        }

        var rec = new GuildStorageItem
        {
            Uid = live.Id, ItemId = live.ItemId, Count = live.Count, Refine = live.Refine,
            Durability = live.Durability, CardsJson = live.CardsJson, DepositedByCharId = live.CharacterId,
            Element = live.Element, DyeColor = live.DyeColor, SocketPunchersJson = live.SocketPunchersJson,
            DyeColor2 = live.DyeColor2, DyeColor3 = live.DyeColor3, FxEnchantIndex = live.FxEnchantIndex,
            EnchantReinforceId = live.EnchantReinforceId, EnchantSeconds = live.EnchantSeconds, Bound = live.Bound,
            AbilityValue = live.AbilityValue,
        };

        if (!await inv.RemoveItemAsync(live.Id, live.CharacterId))
            return (null, VaultDepositRefusal.NotMoved);

        VaultStoreOutcome stored;
        try
        {
            stored = await vault.StoreAsync(guild, rec, capacity);
        }
        catch
        {
            await RestoreToBagAsync(guild, live, rec, inv);
            return (null, VaultDepositRefusal.NotMoved);
        }
        switch (stored)
        {
            case VaultStoreOutcome.Stored: return (rec, VaultDepositRefusal.None);
            case VaultStoreOutcome.Full:
                await RestoreToBagAsync(guild, live, rec, inv);
                return (null, VaultDepositRefusal.VaultFull);
            default:
                await RestoreToBagAsync(guild, live, rec, inv);
                return (null, VaultDepositRefusal.NotMoved);
        }
    }

    /// <summary>Puts the item back into the bag under its original uid and removes it from the vault if it was added there.</summary>
    private async Task RestoreToBagAsync(Guild guild, InventoryItem live, GuildStorageItem rec, IInventoryRepository inv)
    {
        live.Id = rec.Uid;
        try { await inv.AddItemAsync(live); } catch { /* best effort: the vault copy is dropped below either way */ }
        vault.Remove(guild, rec.Uid);
    }

    /// <summary>Moves the record in <paramref name="storageSlot"/> into <paramref name="toCharId"/>'s bag; null when the cell is empty or the add failed (the record is put back).</summary>
    public async Task<GuildStorageItem?> WithdrawAsync(Guild guild, byte storageSlot, uint toCharId, IInventoryRepository inv)
    {
        var rec = vault.Take(guild, storageSlot);
        if (rec == null) return null;

        await vault.PersistAsync(guild);
        try
        {
            var item = new InventoryItem
            {
                Id = rec.Uid, CharacterId = toCharId, ItemId = rec.ItemId, Count = rec.Count <= 0 ? 1 : rec.Count,
                Slot = UnplacedSlot, StorageType = StorageBag, Refine = rec.Refine, Durability = rec.Durability, CardsJson = rec.CardsJson,
                Element = rec.Element, DyeColor = rec.DyeColor, SocketPunchersJson = rec.SocketPunchersJson,
                DyeColor2 = rec.DyeColor2, DyeColor3 = rec.DyeColor3, FxEnchantIndex = rec.FxEnchantIndex,
                EnchantReinforceId = rec.EnchantReinforceId, EnchantSeconds = rec.EnchantSeconds, Bound = rec.Bound,
                AbilityValue = rec.AbilityValue,
            };
            await inv.AddItemAsync(item);
        }
        catch
        {
            vault.PutBack(guild, rec);
            try { await vault.PersistAsync(guild); } catch { /* the in-memory restore stands */ }
            return null;
        }
        return rec;
    }
}
