using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1486 ReqGuildStorageItemGet: withdraws one item from the guild vault into the requester's bag.
/// </summary>
/// <remarks>
/// <para>
/// The request is 20 bytes: [16-byte guild GUID][u8 destination bag][u8 destination slot][u8 vault slot][u8 no destination]. The item is moved back into the requester's bag with its UID unchanged. The answers are 2333 OnPickupItem (places the item in the bag window), 2671 (result), and 2676 NfyDelGuildStorage_Item ([u8 vault slot]), which removes the item from the vault window.
/// </para>
/// <para>
/// The 2676 goes to the withdrawer and to every other guild member whose vault window is open (<see cref="GuildVaultWindows"/>). The withdrawer's copy is sent first.
/// </para>
/// </remarks>
public class GuildStorageItemGetHandler : ZoneRmiHandlerBase<GuildStorageItemGetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildStorageItemGet;
    public override string Name => "GuildStorageItemGet";
    public GuildStorageItemGetHandler(IServiceScopeFactory s, ILogger<GuildStorageItemGetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Result 31: the vault slot holds no item.</summary>
    private const ushort ResultVaultSlotEmpty = 31;

 /// <summary>Result 41: no free bag cell is available.</summary>
    private const ushort ResultBagFull = 41;

 /// <summary>Pickup message selector for a guild withdrawal (2333 body offset 2). Zero is a plain pickup.</summary>
    private const ushort PickupKindGuildWithdraw = 1;

    
    
    
    
    private const int MainBagCells = 20;
    private const int ExtraBagStride = 35;
    private const int ExtraBagCount = 3;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 20) return;   
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var span = payload.Span;
        byte dstBag = span[16];
        byte dstSlot = span[17];
        byte vaultSlot = span[18];
        
        
        
        bool hasDest = span[19] == 0;

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = 0;
        GuildStorageItem? taken = null;
        InventoryItem? added = null;
        short cell = -1;

        if (guild == null || !VaultHolds(guild, vaultSlot)) result = ResultVaultSlotEmpty;
        else
        {
            var inv = services.GetRequiredService<IInventoryRepository>();
            var items = services.GetRequiredService<GameDataStore>().Items;
            var all = await inv.GetByCharacterAsync(player.CharacterId);
            cell = ChooseCell(all, items, hasDest ? BagSlotToFlat(dstBag, dstSlot) : -1);
            if (cell < 0) result = ResultBagFull;   
            else
            {
                
                
                
                var observed = new AddObservingInventory(inv, cell);
                taken = await GuildService.Instance.WithdrawAsync(guild, vaultSlot, player.CharacterId, observed);
                added = observed.Added;
                if (taken == null || added == null) result = ResultVaultSlotEmpty;   
            }
        }

        Logger.LogInformation("Session {Id}: {Name} withdraws vault slot {Slot} -> result {Result} item {Item} to flat {Cell}",
            session.SessionId, player.Name, vaultSlot, result, taken?.ItemId ?? 0, cell);

        if (result == 0 && added != null)
        {
            
            var rec = WorldEntryService.BuildItemRecord126(added);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                w => w.WriteBytes(LootNotifyPackets.BuildPickupItem2333(rec, resultCode: 0, pickupKind: PickupKindGuildWithdraw)));
        }

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildStorageItemGet, writer => writer.WriteUInt16(result));

        if (result == 0 && guild != null && taken != null)
        {
            
            var body = GuildPackets.BuildNfyDelGuildStorageItem(taken.StorageSlot);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyDelGuildStorage_Item, (ReadOnlyMemory<byte>)body);
            await GuildVaultWindows.SendToOtherViewersAsync(guild, session.SessionId, PlayerManager, Broadcast,
                ZoneS2COpcodes.NfyDelGuildStorage_Item, body);
            
            await services.GetRequiredService<IGuildStorageLog>().AddAsync(GuildStorageLog.Guid16Of(guild), player.Name, GuildStorageLog.KindWithdraw, taken.ItemId, taken.Count);
        }
    }

    private static bool VaultHolds(Guild g, byte vaultSlot)
    {
        lock (g.Gate) return g.Storage.Any(s => s.StorageSlot == vaultSlot);
    }

 /// <summary>
 /// Returns the requested bag cell when it is free, otherwise the lowest usable free cell, or -1 when the main bag and every equipped bag are full.
 /// </summary>
    private static short ChooseCell(IReadOnlyList<InventoryItem> all, IReadOnlyDictionary<uint, ItemData> items, int requested)
    {
        var used = new HashSet<int>(all.Where(i => i.StorageType == 0 && i.Slot >= 0).Select(i => (int)i.Slot));
        if (requested >= 0 && !used.Contains(requested) && IsUsableCell(all, items, requested))
            return (short)requested;

        for (int f = 0; f < MainBagCells; f++)
            if (!used.Contains(f)) return (short)f;
        foreach (var bag in all.Where(i => i.StorageType == 3 && i.Slot >= 0 && i.Slot < ExtraBagCount).OrderBy(i => i.Slot))
        {
            int size = BagSize(items, bag);
            for (int k = 0; k < size; k++)
            {
                int f = MainBagCells + ExtraBagStride * bag.Slot + k;
                if (!used.Contains(f)) return (short)f;
            }
        }
        return -1;
    }

    private static bool IsUsableCell(IReadOnlyList<InventoryItem> all, IReadOnlyDictionary<uint, ItemData> items, int flat)
    {
        if (flat < MainBagCells) return true;
        int n = (flat - MainBagCells) / ExtraBagStride, k = (flat - MainBagCells) % ExtraBagStride;
        var bag = all.FirstOrDefault(i => i.StorageType == 3 && i.Slot == n);
        return n < ExtraBagCount && bag != null && k < BagSize(items, bag);
    }

    private static int BagSize(IReadOnlyDictionary<uint, ItemData> items, InventoryItem bag)
        => items.TryGetValue(bag.ItemId, out var data) ? Math.Clamp(data.Bag_Size, 0, ExtraBagStride) : 0;

 /// <summary>
 /// Converts the client's (bag, slot) into a flat inventory slot: container 1 slots 50 to 69 map to 0 to 19, and containers 2 to 4 slots 0 to 34 map to 20 + (bag - 2) * 35 + slot. Returns -1 for anything that is not a bag cell.
 /// </summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return MainBagCells + (bag - 2) * ExtraBagStride + slot;
        return -1;
    }

 /// <summary>
 /// Inventory repository stand-in that places the item passed to <c>AddItemAsync</c> on the chosen cell and keeps it.
 /// </summary>
    private sealed class AddObservingInventory : IInventoryRepository
    {
        private readonly IInventoryRepository _inner;
        private readonly short _cell;
        public InventoryItem? Added { get; private set; }

        public AddObservingInventory(IInventoryRepository inner, short cell)
        {
            _inner = inner;
            _cell = cell;
        }

        public async Task AddItemAsync(InventoryItem item)
        {
            if (item.Slot < 0 && item.StorageType == 0)
                item.Slot = _cell;
            await _inner.AddItemAsync(item);
            Added = item;
        }

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => _inner.GetByCharacterAsync(characterId);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => _inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => _inner.GetByIdAsync(id);
        public Task RemoveItemAsync(ulong itemId) => _inner.RemoveItemAsync(itemId);
        public Task UpdateItemAsync(InventoryItem item) => _inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => _inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => _inner.RepairSlotsAsync(characterId);
    }
}
