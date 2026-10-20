using System.Runtime.CompilerServices;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Buy-back list of a player: the stacks sold to an NPC shop during the current zone session, each with the price paid, so that they can be bought back for the same zeny.
/// </summary>
/// <remarks>
/// The uids, oldest first, make up the sold-item list sent in 2376 OnSellItem. The whole item is kept, because a fully sold stack is removed from the inventory. A uid can be bought back once; a repeat request for it is refused. The list is per zone session.
/// </remarks>
internal static class BuyBackLedger
{
 /// <summary>Number of entries in the sold-item list.</summary>
    internal const int Slots = 10;

 /// <summary>
 /// One sold stack: <see cref="Item"/> is a copy taken at the sale with <see cref="InventoryItem.Count"/> set to the quantity sold, and <see cref="Paid"/> is the zeny the sale paid.
 /// </summary>
    internal sealed record Entry(InventoryItem Item, long Paid);

    private static readonly ConditionalWeakTable<PlayerState, List<Entry>> Ledgers = new();

 /// <summary>
 /// Records a sale. The stack is copied with its count set to the quantity sold.
 /// </summary>
 /// <remarks>
 /// A uid sold again after a buy-back is appended to the end of the list. A partly sold stack keeps its uid, so the newer entry replaces the older one.
 /// </remarks>
 /// <param name="player">Seller's player state.</param>
 /// <param name="stack">Stack that was sold.</param>
 /// <param name="soldQty">Quantity sold.</param>
 /// <param name="paid">Zeny the sale paid.</param>
    internal static void RecordSale(PlayerState player, InventoryItem stack, int soldQty, long paid)
    {
        var copy = new InventoryItem
        {
            Id = stack.Id,
            CharacterId = stack.CharacterId,
            ItemId = stack.ItemId,
            Count = soldQty,
            Slot = stack.Slot,
            StorageType = stack.StorageType,
            CreatedAt = stack.CreatedAt,
        }.CopyInstanceStateFrom(stack); 
        var list = Ledgers.GetValue(player, _ => new List<Entry>());
        lock (list)
        {
            
            list.RemoveAll(e => e.Item.Id == copy.Id);
            list.Add(new Entry(copy, paid));
            if (list.Count > Slots) list.RemoveAt(0);
        }
    }

 /// <summary>Gets the uids on the player's buy-back list, oldest first.</summary>
 /// <param name="player">Player state.</param>
    internal static ulong[] UidsOf(PlayerState player)
    {
        if (!Ledgers.TryGetValue(player, out var list)) return Array.Empty<ulong>();
        lock (list) return list.Select(e => e.Item.Id).ToArray();
    }

 /// <summary>
 /// Removes <paramref name="uid"/> from the list. The removal is atomic, so two requests for the same uid cannot both succeed.
 /// </summary>
    internal static bool TryTake(PlayerState player, ulong uid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry, out int index)
    {
        entry = null;
        index = -1;
        if (!Ledgers.TryGetValue(player, out var list)) return false;
        lock (list)
        {
            index = list.FindIndex(e => e.Item.Id == uid);
            if (index < 0) return false;
            entry = list[index];
            list.RemoveAt(index);
            return true;
        }
    }

 /// <summary>Puts an entry back where it was, when a buy-back does not complete.</summary>
 /// <param name="player">Player state.</param>
 /// <param name="entry">Entry to restore.</param>
 /// <param name="index">Position to restore the entry to.</param>
    internal static void Restore(PlayerState player, Entry entry, int index)
    {
        var list = Ledgers.GetValue(player, _ => new List<Entry>());
        lock (list)
        {
            if (list.Exists(e => e.Item.Id == entry.Item.Id)) return; 
            list.Insert(Math.Clamp(index, 0, list.Count), entry);
            if (list.Count > Slots) list.RemoveAt(0);
        }
    }
}
