using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>Durability wear reads the equipment rows under the character's inventory gate, so a concurrent item swap is never overwritten by a stale row.</summary>
public class DurabilityGateTests
{
    [Fact]
    public async Task WeaponWear_ReadsEquipmentOnlyAfterTheCharacterGateIsFree()
    {
        const uint charId = 94001;
        await using var net = await Harness.StartAsync(1);
        var broadcast = new BroadcastService(new PlayerStateManager(new NoDirtySink()), NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var repo = new ReadSignalInventory();
        var collection = new ServiceCollection();
        collection.AddScoped<IInventoryRepository>(_ => repo);
        await using var services = collection.BuildServiceProvider();
        var durability = new DurabilityService(new GameDataStore(), NullLogger<DurabilityService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), broadcast);
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, CharacterId = charId };

        var release = new TaskCompletionSource();
        var held = new TaskCompletionSource();
        var holder = CharacterInventoryGate.RunAsync(charId, async () => { held.SetResult(); await release.Task; return 0; });
        await held.Task;

        var wear = durability.ApplyWeaponWearOnHitAsync(net.Sessions[0], player, services);
        await Task.Delay(200);
        Assert.False(repo.Read.Task.IsCompleted);   // blocked behind the gate, nothing read yet

        release.SetResult();
        await holder;
        await wear.WaitAsync(Generous);
        Assert.True(repo.Read.Task.IsCompleted);    // read happened after release
    }

    private sealed class ReadSignalInventory : IInventoryRepository
    {
        public TaskCompletionSource Read { get; } = new();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId)
        {
            Read.TrySetResult();
            return Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        }
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult<InventoryItem?>(null);
        public Task AddItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task RemoveItemAsync(ulong itemId) => Task.CompletedTask;
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
