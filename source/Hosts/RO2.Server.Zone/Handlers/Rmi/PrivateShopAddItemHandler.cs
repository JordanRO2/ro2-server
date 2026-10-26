using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1474 ReqAddPrivateShopItem: lists an inventory item in the private shop.</summary>
/// <remarks>
/// Request: 12 bytes, <c>[u8 bag][u8 slot][u16 count][u64 price]</c>.
/// Answer: 2647 AnsAddPrivateShopItem, 27 bytes, <c>[u16 result][u8 index][u8 bag][u8 slot][u64 uid][u16 count][u64 price][u32 itemId]</c>.
/// Refusals use result 23006 (generic refusal; the remaining 25 bytes are zero) or result 23008 (the shop's
/// total listed price would exceed the wallet cap; the item is echoed back).
/// Items that are not sellable, not tradable or bound are refused, mirroring the client's own check.
/// A payload shorter than 12 bytes is dropped without an answer.
/// </remarks>
public class PrivateShopAddItemHandler : ZoneRmiHandlerBase<PrivateShopAddItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAddPrivateShopItem;
    public override string Name => "PrivateShopAddItem";
    public PrivateShopAddItemHandler(IServiceScopeFactory s, ILogger<PrivateShopAddItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>2647 result of a generic refusal (23006).</summary>
    internal const ushort ResultAddRefused = 23006;

 /// <summary>2647 result of a listing that would push the shop's total price past <see cref="GuildFundsInHandPackets.WalletCap"/> (23008).</summary>
    internal const ushort ResultTotalPriceOverLimit = 23008;

 /// <summary>Size in bytes of the 2647 answer.</summary>
    internal const int AnswerBytes = 27;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 12) return;
        var span = payload.Span;
        byte bag = span[0];
        byte slot = span[1];
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
        ulong price = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(4, 8));

        int flat = BagSlotToFlat(bag, slot);
        var inv = services.GetRequiredService<IInventoryRepository>();
        var item = flat < 0 ? null
            : (await inv.GetByCharacterAsync(player.CharacterId)).FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        var data = services.GetRequiredService<GameDataStore>();
        var template = item == null ? null : data.GetItem(item.ItemId);

        if (item == null || count == 0 || count > item.Count || !ClientGateAllows(template, item))
        {
            Logger.LogWarning("{Name}: private shop listing of bag {Bag}/{Slot} x{Count} refused (item {Found}, stack {Stack}, sell {Sell}, trade {Trade}, bound {Bound})",
                player.Name, bag, slot, count, item?.Id.ToString() ?? "none", item?.Count ?? 0,
                template?.Is_Sell, template?.Is_Trade, item != null && template != null && IsBound(template, item));
            await session.SendRmiAsync(ZoneS2COpcodes.AnsAddPrivateShopItem, BuildRefused());
            return;
        }

        
        
        
        
        var listed = PrivateShopRegistry.TryGet(player, out _, out var listings) ? listings : new List<PrivateShopRegistry.Listing>();
        long cap = GuildFundsInHandPackets.WalletCap(data.ConfigDef);
        if (!listed.Any(l => l.Uid == item.Id) && ExceedsTotalPriceCap(listed.Select(l => l.Price), price, cap))
        {
            Logger.LogWarning("{Name}: private shop listing of item {Uid} at {Price} refused, total would pass {Cap} ({Listed} listed)",
                player.Name, item.Id, price, cap, listed.Count);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsAddPrivateShopItem,
                BuildTotalPriceRefused(bag, slot, item.Id, count, price, item.ItemId));
            return;
        }

        if (!PrivateShopRegistry.TryAdd(player, item, count, price, out var listing))
        {
            Logger.LogWarning("{Name}: private shop listing of bag {Bag}/{Slot} refused, item {Uid} is already listed",
                player.Name, bag, slot, item.Id);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsAddPrivateShopItem, BuildRefused());
            return;
        }

        Logger.LogInformation("{Name} lists item {Uid} (id {ItemId}) x{Count} at {Price} in private shop slot {Index}",
            player.Name, item.Id, item.ItemId, count, price, listing.Index);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsAddPrivateShopItem,
            BuildAdded(listing.Index, bag, slot, item.Id, count, price, item.ItemId));
    }

 /// <summary>True when the item is sellable and tradable and not bound, as the client's own drop check requires.</summary>
    internal static bool ClientGateAllows(ItemData? template, InventoryItem item)
        => template is not null && template.Is_Sell != 0 && template.Is_Trade != 0 && !IsBound(template, item);

 /// <summary>True when the item record's bind bit is set.</summary>
    private static bool IsBound(ItemData template, InventoryItem item)
        => ItemPackets.RecordBindFlags(template.Bind_Type, item.StorageType, item.Bound) == ItemPackets.RecordBoundFlag;

 /// <summary>The 27-byte refusal 2647: result 23006 followed by 25 zero bytes.</summary>
    internal static byte[] BuildRefused()
    {
        var body = new byte[AnswerBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(body, ResultAddRefused);
        return body;
    }

 /// <summary>
 /// True when listing one more item at <paramref name="price"/> would push the sum of the listed prices past
 /// <paramref name="cap"/>. A sum equal to the cap is allowed. The sum is computed in 128 bits to avoid overflow.
 /// </summary>
    internal static bool ExceedsTotalPriceCap(IEnumerable<ulong> listedPrices, ulong price, long cap)
    {
        UInt128 total = price;
        foreach (var p in listedPrices) total += p;
        return total > (UInt128)(ulong)Math.Max(0L, cap);
    }

 /// <summary>The 27-byte 23008 refusal 2647, echoing the refused item so the client deselects it.</summary>
    internal static byte[] BuildTotalPriceRefused(byte bag, byte slot, ulong uid, ushort count, ulong price, uint itemId)
        => Build(ResultTotalPriceOverLimit, 0, bag, slot, uid, count, price, itemId);

 /// <summary>The 27-byte success 2647.</summary>
    internal static byte[] BuildAdded(byte index, byte bag, byte slot, ulong uid, ushort count, ulong price, uint itemId)
        => Build(0, index, bag, slot, uid, count, price, itemId);

    private static byte[] Build(ushort result, byte index, byte bag, byte slot, ulong uid, ushort count, ulong price, uint itemId)
    {
        using var w = new PacketWriter(AnswerBytes);
        w.WriteUInt16(result);     
        w.WriteByte(index);        
        w.WriteByte(bag);          
        w.WriteByte(slot);         
        w.WriteUInt64(uid);        
        w.WriteUInt16(count);      
        w.WriteUInt64(price);      
        w.WriteUInt32(itemId);     
        return w.ToArray();
    }

 /// <summary>Maps a client (bag, slot) cell to a flat bag slot; returns -1 for cells that are not in the bag.</summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }
}
