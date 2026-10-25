using System.Runtime.CompilerServices;
using RO2.Domain.Entities;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host side of the per-player buy-back list: it keeps one <see cref="BuyBackList"/> per player and lets the handlers
/// and the shop application flows reach it.
/// </summary>
/// <remarks>
/// The list itself (what is recorded, the ten slots, take and restore) is <see cref="BuyBackList"/> in the economy
/// application; this holds only the player to list association and maps the game's inventory row onto the neutral
/// snapshot the list stores.
/// </remarks>
internal static class BuyBackLedger
{
    /// <summary>Number of entries in the sold-item list.</summary>
    internal const int Slots = BuyBackList.Slots;

    private static readonly ConditionalWeakTable<PlayerState, BuyBackList> Lists = new();

    /// <summary>The player's buy-back list, created empty on first use.</summary>
    internal static BuyBackList ListOf(PlayerState player) => Lists.GetValue(player, _ => new BuyBackList());

    /// <summary>Records a sale on the player's list; see <see cref="BuyBackList.RecordSale"/>.</summary>
    /// <param name="player">Seller's player state.</param>
    /// <param name="stack">Stack that was sold.</param>
    /// <param name="soldQty">Quantity sold.</param>
    /// <param name="paid">Zeny the sale paid.</param>
    internal static void RecordSale(PlayerState player, InventoryItem stack, int soldQty, long paid)
        => ListOf(player).RecordSale(InventoryEscrowAdapter.ToSnapshot(stack), soldQty, paid);

    /// <summary>Gets the uids on the player's buy-back list, oldest first; empty when the player has none yet.</summary>
    /// <param name="player">Player state.</param>
    internal static ulong[] UidsOf(PlayerState player)
        => Lists.TryGetValue(player, out var list) ? list.Uids() : Array.Empty<ulong>();

    /// <summary>Removes <paramref name="uid"/> from the player's list; see <see cref="BuyBackList.TryTake"/>.</summary>
    internal static bool TryTake(PlayerState player, ulong uid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BuyBackEntry? entry, out int index)
    {
        entry = null;
        index = -1;
        return Lists.TryGetValue(player, out var list) && list.TryTake(uid, out entry, out index);
    }

    /// <summary>Puts an entry back where it was, when a buy-back does not complete.</summary>
    /// <param name="player">Player state.</param>
    /// <param name="entry">Entry to restore.</param>
    /// <param name="index">Position to restore the entry to.</param>
    internal static void Restore(PlayerState player, BuyBackEntry entry, int index)
        => ListOf(player).Restore(entry, index);
}
