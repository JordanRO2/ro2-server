using RO2.Domain.Entities;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Quantity offered per stack in a trade window, as sent in 1353 ReqPCTradeMove.
/// The quantities live on the <see cref="TradeSession"/> and share its lifetime, so cancelled or completed trades drop them.
/// The confirm-time swap moves exactly the offered quantity.
/// </summary>
public static class TradeOfferQuantities
{
 /// <summary>Records, or replaces, the offered quantity for an item uid in this trade.</summary>
    public static void Set(TradeSession trade, ulong uid, int quantity) =>
        trade.RecordQuantity(uid, quantity);

 /// <summary>The offered quantity for an item uid, or null when none was recorded.</summary>
    public static int? Get(TradeSession trade, ulong uid) =>
        trade.QuantityOf(uid);

 /// <summary>
 /// A detached copy of the item with the given stack size, for building the trade window record without changing the stored item.
 /// </summary>
    public static InventoryItem WithCount(InventoryItem item, int count) => new InventoryItem
    {
        Id = item.Id,
        CharacterId = item.CharacterId,
        ItemId = item.ItemId,
        Count = count,
        Slot = item.Slot,
        StorageType = item.StorageType,
        CreatedAt = item.CreatedAt,
    }.CopyInstanceStateFrom(item); 
}
