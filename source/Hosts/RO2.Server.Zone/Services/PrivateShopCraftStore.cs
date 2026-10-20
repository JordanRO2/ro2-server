using System.Runtime.CompilerServices;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Session-level access to a seller's private-shop craft offers, which persist per character across sessions.
/// Storage is <see cref="IPrivateShopCraftRepository"/>; this holder applies the session rules.
/// </summary>
/// <remarks>
/// Offers are loaded once per zone session into <c>PrivateShopRegistry</c> by <see cref="EnsureLoadedAsync"/>, which the
/// seller's open, add and delete handlers call. The list is saved whole after each change. A re-added recipe keeps its
/// place in the list and takes the new price.
/// </remarks>
internal static class PrivateShopCraftStore
{
 /// <summary>One gate per seller's zone session. The load and every change, snapshot and save run under it,
 /// so changes are stored in order and never land before the load.</summary>
    private static readonly ConditionalWeakTable<PlayerState, SemaphoreSlim> Gates = new();

 /// <summary>Loads the seller's stored offers into the registry, once per zone session.</summary>
    internal static async Task EnsureLoadedAsync(PlayerState seller, IPrivateShopCraftRepository repo, CancellationToken ct)
    {
        var gate = GateOf(seller);
        await gate.WaitAsync(ct);
        try { await LoadIfNeededAsync(seller, repo, ct); }
        finally { gate.Release(); }
    }

 /// <summary>Offers <paramref name="craftId"/> at <paramref name="price"/> and stores the new list.</summary>
    internal static async Task SetAsync(PlayerState seller, ushort craftId, ulong price, IPrivateShopCraftRepository repo, CancellationToken ct)
    {
        var gate = GateOf(seller);
        await gate.WaitAsync(ct);
        try
        {
            await LoadIfNeededAsync(seller, repo, ct);
            PrivateShopRegistry.SetCraft(seller, craftId, price);
            await repo.ReplaceOffersAsync(seller.CharacterId, PrivateShopRegistry.GetCrafts(seller), ct);
        }
        finally { gate.Release(); }
    }

 /// <summary>Withdraws the offer for <paramref name="craftId"/> and stores the new list. Returns false, and stores
 /// nothing, when the craft was not on offer.</summary>
    internal static async Task<bool> RemoveAsync(PlayerState seller, ushort craftId, IPrivateShopCraftRepository repo, CancellationToken ct)
    {
        var gate = GateOf(seller);
        await gate.WaitAsync(ct);
        try
        {
            await LoadIfNeededAsync(seller, repo, ct);
            if (!PrivateShopRegistry.RemoveCraft(seller, craftId)) return false;
            await repo.ReplaceOffersAsync(seller.CharacterId, PrivateShopRegistry.GetCrafts(seller), ct);
            return true;
        }
        finally { gate.Release(); }
    }

    private static SemaphoreSlim GateOf(PlayerState seller) => Gates.GetValue(seller, _ => new SemaphoreSlim(1, 1));

    private static async Task LoadIfNeededAsync(PlayerState seller, IPrivateShopCraftRepository repo, CancellationToken ct)
    {
        if (PrivateShopRegistry.AreCraftsLoaded(seller)) return;
        var stored = await repo.GetOffersAsync(seller.CharacterId, ct);
        PrivateShopRegistry.LoadCrafts(seller, stored);
    }
}
