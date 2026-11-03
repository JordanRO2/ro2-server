using System.Text;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// The private-shop buy's item moves: both characters' inventory gates are held around the whole buy, and every write
/// is an owner-guarded atomic one, so a row that changed or left between the read and the write is neither overwritten
/// nor silently skipped.
/// </summary>
public sealed class StallBuyInventoryAdapterTests
{
    private const uint SellerId = 7010, BuyerId = 7020, Potion = 11100004;

    /// <summary>Runs <paramref name="afterRead"/> right after each read of a character's rows, i.e. between the buy's read and its write.</summary>
    private sealed class ChangesAfterRead(FakeInventoryRepository inner, Action<uint> afterRead) : IInventoryRepository
    {
        public async Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId)
        {
            var rows = await inner.GetByCharacterAsync(characterId);
            afterRead(characterId);
            return rows;
        }
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => inner.GetByIdAsync(id);
        public Task AddItemAsync(InventoryItem item) => inner.AddItemAsync(item);
        public Task RemoveItemAsync(ulong itemId) => inner.RemoveItemAsync(itemId);
        public Task<bool> RemoveItemAsync(ulong itemId, uint characterId) => inner.RemoveItemAsync(itemId, characterId);
        public Task UpdateItemAsync(InventoryItem item) => inner.UpdateItemAsync(item);
        public Task<int?> AdjustCountAsync(ulong itemId, uint characterId, int delta) => inner.AdjustCountAsync(itemId, characterId, delta);
        public Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
            => inner.TryReassignAsync(itemId, fromCharId, toCharId, toStorageType, toSlot);
        public Task<ulong> GetNextIdAsync() => inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => inner.RepairSlotsAsync(characterId);
    }

    private static readonly Dictionary<uint, ItemData> Items = new() { [Potion] = new ItemData { ID = Potion, Stack_Max = 20 } };

    private static (EconomyFixture Fx, PlayerState Owner, PlayerState Buyer, PrivateShopRegistry.Listing Listing) Shop(
        int sellerCount, int listed, int? buyerHeld = null)
    {
        var fx = new EconomyFixture();
        var owner = fx.AddPlayer(SellerId, gold: 0);
        var buyer = fx.AddPlayer(BuyerId, gold: 1000);
        PrivateShopRegistry.SetName(owner, Encoding.Unicode.GetBytes("shop"));
        PrivateShopRegistry.AddViewer(owner, buyer);
        var stack = fx.GiveItem(SellerId, Potion, sellerCount, slot: 3);
        if (buyerHeld is { } held) fx.GiveItem(BuyerId, Potion, held, slot: 0);
        Assert.True(PrivateShopRegistry.TryAdd(owner, stack, (ushort)listed, 100, out var listing));
        return (fx, owner, buyer, listing);
    }

    [Fact]
    public async Task Buy_WaitsForTheSellersInventoryGate_ThenCompletes()
    {
        var (fx, owner, buyer, listing) = Shop(sellerCount: 3, listed: 3);
        var release = new TaskCompletionSource();
        var held = new TaskCompletionSource();
        var holder = CharacterInventoryGate.RunAsync(SellerId, async () => { held.SetResult(); await release.Task; return 0; });
        await held.Task;

        var buy = PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, [listing.Index], fx.InventoryRepo, Items);
        await Task.Delay(200);
        Assert.False(buy.IsCompleted);   // e.g. the seller is selling that stack to an NPC under this gate right now

        release.SetResult();
        await holder;
        Assert.Equal(0, (await buy).Refusal);
        Assert.Equal(BuyerId, fx.Inventory.Rows.Single().CharacterId);
    }

    [Fact]
    public async Task WholeStack_ThatLeftTheSellerBetweenReadAndWrite_ThrowsInsteadOfDeliveringNothing()
    {
        var (fx, owner, buyer, listing) = Shop(sellerCount: 3, listed: 3);
        var inventory = new ChangesAfterRead(fx.Inventory, id =>
        {
            if (id == SellerId) fx.Inventory.RemoveItemAsync(fx.Inventory.Rows.Single(r => r.CharacterId == SellerId).Id).GetAwaiter().GetResult();
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, [listing.Index], inventory, Items));

        Assert.Empty(fx.Inventory.Rows);   // no phantom row for the buyer
    }

    [Fact]
    public async Task MergeIntoTheBuyersHeldStack_AddsToTheLiveCount_NotTheSnapshotCount()
    {
        var (fx, owner, buyer, listing) = Shop(sellerCount: 5, listed: 2, buyerHeld: 5);
        var inventory = new ChangesAfterRead(fx.Inventory, id =>
        {
            if (id == BuyerId)   // a loot pickup lands on the buyer's stack after the buy read it
                fx.Inventory.AdjustCountAsync(fx.Inventory.Rows.Single(r => r.CharacterId == BuyerId).Id, BuyerId, 3).GetAwaiter().GetResult();
        });

        var sale = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, [listing.Index], inventory, Items);

        Assert.Equal(0, sale.Refusal);
        Assert.Equal(5 + 3 + 2, fx.Inventory.Rows.Single(r => r.CharacterId == BuyerId).Count);   // the +3 survives
        Assert.Equal(3, fx.Inventory.Rows.Single(r => r.CharacterId == SellerId).Count);
    }
}
