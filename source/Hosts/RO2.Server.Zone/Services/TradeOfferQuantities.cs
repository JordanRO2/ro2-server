using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Quantity offered per stack in a trade window, as sent in 1353 ReqPCTradeMove.
/// Kept beside <see cref="TradeSession"/> and tied to its lifetime, so cancelled or completed trades drop their quantities.
/// The confirm-time swap moves exactly the offered quantity.
/// </summary>
public static class TradeOfferQuantities
{
    private static readonly ConditionalWeakTable<TradeSession, ConcurrentDictionary<ulong, int>> Tables = new();

 /// <summary>Records, or replaces, the offered quantity for an item uid in this trade.</summary>
    public static void Set(TradeSession trade, ulong uid, int quantity) =>
        Tables.GetOrCreateValue(trade)[uid] = quantity;

 /// <summary>The offered quantity for an item uid, or null when none was recorded.</summary>
    public static int? Get(TradeSession trade, ulong uid) =>
        Tables.TryGetValue(trade, out var t) && t.TryGetValue(uid, out var q) ? (int?)q : null;

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
