using RO2.BuildingBlocks;
using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>
/// The buy-back list of one player: the stacks sold to an NPC shop during the current zone session, each with the
/// price paid, so that they can be bought back for the same zeny. Pure state behind a lock; the host keeps one per
/// player.
/// </summary>
/// <remarks>
/// The uids, oldest first, make up the sold-item list sent in 2376 OnSellItem. The whole item is kept, because a
/// fully sold stack is removed from the inventory. A uid can be bought back once; a repeat request for it is refused.
/// The list is per zone session.
/// </remarks>
public sealed class BuyBackList
{
    /// <summary>Number of entries in the sold-item list.</summary>
    public const int Slots = 10;

    private readonly List<BuyBackEntry> _entries = [];

    /// <summary>
    /// Records a sale. The stack is copied with its count set to the quantity sold.
    /// </summary>
    /// <remarks>
    /// A uid sold again after a buy-back is appended to the end of the list. A partly sold stack keeps its uid, so the
    /// newer entry replaces the older one.
    /// </remarks>
    /// <param name="stack">Stack that was sold.</param>
    /// <param name="soldQty">Quantity sold.</param>
    /// <param name="paid">Zeny the sale paid.</param>
    public void RecordSale(ItemStackSnapshot stack, int soldQty, long paid)
    {
        var copy = stack with { Count = soldQty };
        lock (_entries)
        {
            _entries.RemoveAll(e => e.Item.Id == copy.Id);
            _entries.Add(new BuyBackEntry(copy, paid));
            if (_entries.Count > Slots) _entries.RemoveAt(0);
        }
    }

    /// <summary>Gets the uids on the list, oldest first.</summary>
    public ulong[] Uids()
    {
        lock (_entries) return _entries.Select(e => e.Item.Id).ToArray();
    }

    /// <summary>
    /// Removes <paramref name="uid"/> from the list. The removal is atomic, so two requests for the same uid cannot
    /// both succeed.
    /// </summary>
    public bool TryTake(ulong uid, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BuyBackEntry? entry, out int index)
    {
        entry = null;
        index = -1;
        lock (_entries)
        {
            index = _entries.FindIndex(e => e.Item.Id == uid);
            if (index < 0) return false;
            entry = _entries[index];
            _entries.RemoveAt(index);
            return true;
        }
    }

    /// <summary>Puts an entry back where it was, when a buy-back does not complete.</summary>
    /// <param name="entry">Entry to restore.</param>
    /// <param name="index">Position to restore the entry to.</param>
    public void Restore(BuyBackEntry entry, int index)
    {
        lock (_entries)
        {
            if (_entries.Exists(e => e.Item.Id == entry.Item.Id)) return;
            _entries.Insert(Math.Clamp(index, 0, _entries.Count), entry);
            if (_entries.Count > Slots) _entries.RemoveAt(0);
        }
    }
}
