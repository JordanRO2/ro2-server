using RO2.Economy.Domain;
using System.Runtime.CompilerServices;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// A player's private shop: the items it lists, the crafts it offers, its title, and the buyers who opened it.
/// </summary>
/// <remarks>
/// The listed items are kept per zone session, and each listing refers to the item instance in the owner's bag.
/// Craft offers belong to the character and persist across sessions (<see cref="PrivateShopCraftStore"/>). The item
/// is read from the owner's bag each time a buyer looks, so an item that was moved, sold or destroyed stops being offered.
/// The list index keeps counting across setup windows. Buyers who opened the shop are kept in a viewer list that is
/// notified when the shop ends, and purchases are accepted only while the shop is open.
/// </remarks>
internal static class PrivateShopRegistry
{
    /// <summary>The shop title field: a wchar_t[21] (42 bytes), at most 20 characters followed by a NUL.
    /// It appears in 1478 and 2651 at +14, in 2663/2664/2665 at blob+14 and in 2646/2657/2658 at blob+2.</summary>
    internal const int NameBytes = 42;

    /// <summary>Storage type of the owner's bag, the only place a listed item is sold from.</summary>
    private const byte BagStorageType = 0;

    // The shop sign categories below are a bit mask at +56 of 2663 and 2651.

    /// <summary>Sign bit: the shop sells items.</summary>
    internal const byte CategoryItems = 8;

    /// <summary>Sign bit: the shop takes craft orders. Set when the shop has crafts on offer.</summary>
    internal const byte CategoryCrafts = 1;

    /// <summary>Sign bit: the shop does random refines.</summary>
    internal const byte CategoryRandomRefine = 4;

    /// <summary>Sign bit: the shop offers the refine service.</summary>
    internal const byte CategoryRefineService = 2;

    /// <summary>Actor state byte (2398 blob+12) of the OnChangeState sent just before the sign when a shop opens.
    /// The state 0 message sent when the shop ends undoes it.</summary>
    internal const byte ShopOpenState = 0x1F;

    /// <summary>Constant at 2398 blob+19 in both shop messages (open and close).</summary>
    internal const uint ShopChangeStateWord19 = 2;

    /// <summary>Constant at 2398 blob+23 in both shop messages; the client stores it on the actor. Its meaning is not established.</summary>
    internal const uint ShopChangeStateWord23 = 7;

    /// <summary>One listed item. <c>Index</c> is the list index given to buyers, <c>Uid</c> the listed item instance,
    /// and <c>Count</c> and <c>Price</c> the listed quantity and price.</summary>
    internal sealed record Listing(byte Index, ulong Uid, ushort Count, ulong Price);

    private sealed class Shop
    {
        internal readonly List<Listing> Items = new();
        internal readonly List<PrivateShopCraftOffer> Crafts = new();
        /// <summary>True once the character's stored offers are loaded into <see cref="Crafts"/> for this zone session.</summary>
        internal bool CraftsLoaded;
        internal byte NextIndex;
        internal byte[] Name = new byte[NameBytes];
        /// <summary>Players who opened this shop since it began, as (SessionId, CharacterId). They are notified when the shop ends.
        /// See <see cref="AddViewer"/>.</summary>
        internal readonly HashSet<(uint SessionId, uint CharacterId)> Viewers = new();
        /// <summary>True from the begin until the end of the shop, the only period in which purchases are accepted.
        /// See <see cref="IsOpen"/>.</summary>
        internal bool Open;
        /// <summary>Serializes the purchases of this shop, so two buyers cannot both take one listing. See <see cref="PurchaseGate"/>.</summary>
        internal readonly SemaphoreSlim Purchases = new(1, 1);
    }

    private static readonly ConditionalWeakTable<PlayerState, Shop> Shops = new();

    /// <summary>The shop a player's buyer window currently shows, as the owner's (SessionId, CharacterId), or null once it was closed.
    /// See <see cref="IsWatching"/>.</summary>
    private sealed class Watch
    {
        internal (uint SessionId, uint CharacterId)? Owner;
    }

    private static readonly ConditionalWeakTable<PlayerState, Watch> Watches = new();

    /// <summary>Begins a setup window for the seller. The item list starts empty; the craft offers are kept.</summary>
    internal static void BeginSetup(PlayerState seller)
    {
        var shop = Shops.GetValue(seller, _ => new Shop());
        lock (shop) shop.Items.Clear();
    }

    /// <summary>
    /// Lists <paramref name="count"/> of <paramref name="item"/> at <paramref name="price"/>. Returns false when that item
    /// instance is already listed, so one stack cannot be offered twice.
    /// </summary>
    internal static bool TryAdd(PlayerState seller, InventoryItem item, ushort count, ulong price, out Listing listing)
    {
        var shop = Shops.GetValue(seller, _ => new Shop());
        lock (shop)
        {
            if (shop.Items.Any(l => l.Uid == item.Id))
            {
                listing = null!;
                return false;
            }
            listing = new Listing(shop.NextIndex, item.Id, count, price);
            shop.NextIndex = unchecked((byte)(shop.NextIndex + 1)); // the list index is a u8 on the wire
            shop.Items.Add(listing);
            return true;
        }
    }

    /// <summary>
    /// Removes the listing with list index <paramref name="index"/>. Returns false when the seller has no such listing.
    /// Indices are not reused.
    /// </summary>
    internal static bool TryRemove(PlayerState seller, byte index)
    {
        if (!Shops.TryGetValue(seller, out var shop)) return false;
        lock (shop) return shop.Items.RemoveAll(l => l.Index == index) > 0;
    }

    /// <summary>
    /// Offers recipe <paramref name="craftId"/> at <paramref name="price"/>. A recipe already on offer keeps its place and
    /// takes the new price, so a recipe appears only once.
    /// </summary>
    internal static void SetCraft(PlayerState seller, ushort craftId, ulong price)
    {
        var shop = Shops.GetValue(seller, _ => new Shop());
        lock (shop)
        {
            int at = shop.Crafts.FindIndex(c => c.CraftId == craftId);
            var offer = new PrivateShopCraftOffer(craftId, price);
            if (at >= 0) shop.Crafts[at] = offer;
            else shop.Crafts.Add(offer);
        }
    }

    /// <summary>
    /// Removes recipe <paramref name="craftId"/> from the seller's offers. Returns false when it was not on offer.
    /// </summary>
    internal static bool RemoveCraft(PlayerState seller, ushort craftId)
    {
        if (!Shops.TryGetValue(seller, out var shop)) return false;
        lock (shop) return shop.Crafts.RemoveAll(c => c.CraftId == craftId) > 0;
    }

    /// <summary>
    /// Loads the character's stored offers, in their stored order, as the seller's offers for this zone session. A recipe
    /// listed twice keeps its first place.
    /// </summary>
    internal static void LoadCrafts(PlayerState seller, IReadOnlyList<PrivateShopCraftOffer> stored)
    {
        var shop = Shops.GetValue(seller, _ => new Shop());
        lock (shop)
        {
            shop.Crafts.Clear();
            foreach (var offer in stored)
                if (!shop.Crafts.Any(c => c.CraftId == offer.CraftId)) shop.Crafts.Add(offer);
            shop.CraftsLoaded = true;
        }
    }

    /// <summary>True when <see cref="LoadCrafts"/> has already run for this zone session.</summary>
    internal static bool AreCraftsLoaded(PlayerState seller)
    {
        if (!Shops.TryGetValue(seller, out var shop)) return false;
        lock (shop) return shop.CraftsLoaded;
    }

    /// <summary>The seller's craft offers, in the order they were added. Empty when there are none.</summary>
    internal static List<PrivateShopCraftOffer> GetCrafts(PlayerState owner)
    {
        if (!Shops.TryGetValue(owner, out var shop)) return new();
        lock (shop) return new List<PrivateShopCraftOffer>(shop.Crafts);
    }

    /// <summary>
    /// Records the title the shop opens with and starts an empty viewer list. The shop is open until it ends.
    /// A rename goes through <see cref="Rename"/>.
    /// </summary>
    internal static void SetName(PlayerState seller, ReadOnlySpan<byte> name42)
    {
        var shop = Shops.GetValue(seller, _ => new Shop());
        var copy = new byte[NameBytes];
        name42[..Math.Min(NameBytes, name42.Length)].CopyTo(copy);
        lock (shop)
        {
            shop.Name = copy;
            shop.Viewers.Clear();
            shop.Open = true;
        }
    }

    /// <summary>
    /// Sets a new title on the open shop. The viewer list is kept. Returns false when the seller has no shop in this zone session.
    /// </summary>
    internal static bool Rename(PlayerState seller, ReadOnlySpan<byte> field)
    {
        if (!Shops.TryGetValue(seller, out var shop)) return false;
        var name = NameFromClient(field);
        lock (shop) shop.Name = name;
        return true;
    }

    /// <summary>
    /// Records that <paramref name="buyer"/> opened <paramref name="owner"/>'s shop, so that it is notified when the shop ends.
    /// The owner is never added to its own list. Does nothing when the owner has no shop in this zone session.
    /// </summary>
    /// <remarks>
    /// A buyer who opened the shop since its latest begin is notified at its end, even if the window has since been closed.
    /// </remarks>
    internal static void AddViewer(PlayerState owner, PlayerState buyer)
    {
        if (ReferenceEquals(owner, buyer) || !Shops.TryGetValue(owner, out var shop)) return;
        lock (shop) shop.Viewers.Add((buyer.SessionId, buyer.CharacterId));
        // The buyer's window now shows this shop and no longer any shop it looked at before (see IsWatching).
        // The end-notice list is left as it is.
        var watch = Watches.GetValue(buyer, _ => new Watch());
        lock (watch) watch.Owner = (owner.SessionId, owner.CharacterId);
    }

    /// <summary>
    /// Closes the buyer window of <paramref name="buyer"/>, so that the shop is no longer watched. The end-notice list is not changed.
    /// </summary>
    /// <remarks>
    /// A closed window must not receive later list updates, since the client would reopen its trade state.
    /// </remarks>
    internal static void StopWatching(PlayerState buyer)
    {
        if (!Watches.TryGetValue(buyer, out var watch)) return;
        lock (watch) watch.Owner = null;
    }

    /// <summary>
    /// True when <paramref name="viewer"/> opened <paramref name="owner"/>'s shop, has not opened another shop since, and has not
    /// closed the window. Such players receive the list update after a sale.
    /// </summary>
    /// <remarks>
    /// A list update for a player who has opened another shop would overwrite that shop's list on the client, so it is not sent.
    /// </remarks>
    internal static bool IsWatching(PlayerState owner, PlayerState viewer)
    {
        if (!IsViewer(owner, viewer) || !Watches.TryGetValue(viewer, out var watch)) return false;
        lock (watch) return watch.Owner == (owner.SessionId, owner.CharacterId);
    }

    /// <summary>The players who opened the shop since it began, without emptying the list. Filter with <see cref="IsWatching"/>.</summary>
    internal static List<(uint SessionId, uint CharacterId)> ViewersSnapshot(PlayerState owner)
    {
        if (!Shops.TryGetValue(owner, out var shop)) return new();
        lock (shop) return shop.Viewers.ToList();
    }

    /// <summary>True from the owner's begin until its end. The shop sells only in that period.</summary>
    internal static bool IsOpen(PlayerState owner)
    {
        if (!Shops.TryGetValue(owner, out var shop)) return false;
        lock (shop) return shop.Open;
    }

    /// <summary>Serializes every wallet change of a private-shop sale, so that two sales touching one wallet cannot lose an update.</summary>
    internal static readonly object WalletLock = new();

    /// <summary>The gate that serializes purchases from <paramref name="owner"/>'s shop, or null when the owner has no shop in this zone session.</summary>
    internal static SemaphoreSlim? PurchaseGate(PlayerState owner) =>
        Shops.TryGetValue(owner, out var shop) ? shop.Purchases : null;

    /// <summary>
    /// True when <paramref name="buyer"/> opened <paramref name="owner"/>'s shop and it has not ended since. Never true for the owner.
    /// </summary>
    /// <remarks>
    /// Craft orders and refine requests that name a shop are accepted only from a buyer in this state.
    /// </remarks>
    internal static bool IsViewer(PlayerState owner, PlayerState buyer)
    {
        if (ReferenceEquals(owner, buyer) || !Shops.TryGetValue(owner, out var shop)) return false;
        lock (shop) return shop.Viewers.Contains((buyer.SessionId, buyer.CharacterId));
    }

    /// <summary>
    /// Ends the shop: returns the players who opened it since it began and empties the list. The shop is no longer open afterwards.
    /// </summary>
    internal static List<(uint SessionId, uint CharacterId)> TakeViewers(PlayerState owner)
    {
        if (!Shops.TryGetValue(owner, out var shop)) return new();
        lock (shop)
        {
            var viewers = shop.Viewers.ToList();
            shop.Viewers.Clear();
            shop.Open = false;
            return viewers;
        }
    }

    /// <summary>The owner's shop as a buyer sees it, or false when the owner has no shop in this zone session.
    /// The listings are returned as recorded; filter them with <see cref="Live"/>.</summary>
    internal static bool TryGet(PlayerState owner, out byte[] name42, out List<Listing> listings)
    {
        name42 = null!;
        listings = null!;
        if (!Shops.TryGetValue(owner, out var shop)) return false;
        lock (shop)
        {
            name42 = (byte[])shop.Name.Clone();
            listings = new List<Listing>(shop.Items);
        }
        return true;
    }

    /// <summary>
    /// The listings that can still be sold, each paired with the item as it now stands: the same instance, still in the owner's
    /// bag, holding at least the listed count.
    /// </summary>
    internal static List<(Listing Listing, InventoryItem Item)> Live(IEnumerable<Listing> listings, IReadOnlyList<InventoryItem> ownerInventory)
    {
        var live = new List<(Listing Listing, InventoryItem Item)>();
        foreach (var l in listings)
        {
            var it = ownerInventory.FirstOrDefault(i => i.Id == l.Uid && i.StorageType == BagStorageType);
            if (it != null && it.Count >= l.Count) live.Add((l, it));
        }
        return live;
    }

    /// <summary>
    /// Converts a client-written title (wchar_t[21]) to the 42-byte field: the characters up to the first NUL, at most 20,
    /// then zeros.
    /// </summary>
    internal static byte[] NameFromClient(ReadOnlySpan<byte> field)
    {
        var name = new byte[NameBytes];
        int max = Math.Min(field.Length, NameBytes - 2) & ~1; // 20 characters, leaving the terminator
        for (int i = 0; i < max; i += 2)
        {
            if (field[i] == 0 && field[i + 1] == 0) break;
            name[i] = field[i];
            name[i + 1] = field[i + 1];
        }
        return name;
    }
}
