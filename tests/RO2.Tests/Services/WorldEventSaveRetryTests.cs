using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Packets;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>A world-event save the store rejected is retried on the next timer pass instead of being lost.</summary>
public class WorldEventSaveRetryTests
{
    private const uint Jawaii = 122101;

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private sealed class FlakyRepo : IWorldEventStateRepository
    {
        public bool Fail;
        public readonly List<WorldEventStateDocument> Saved = new();

        public Task<IReadOnlyList<WorldEventStateDocument>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorldEventStateDocument>>(new[]
            {
                new WorldEventStateDocument { Id = Jawaii, Stage = 2, StageEnteredUtc = DateTime.UtcNow, Counts = new List<uint> { 0, 0 } },
            });

        public Task SaveAsync(WorldEventStateDocument doc, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("store down");
            Saved.Add(doc);
            return Task.CompletedTask;
        }
    }

    private static GameDataStore StoreWithWorldEventTables()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var store = new GameDataStore();
        void Set<T>(string name, T value) => typeof(GameDataStore).GetProperty(name)!.SetValue(store, value);
        Set<IReadOnlyList<WorldEventMain>>(nameof(GameDataStore.WorldEventMain), t.Main);
        Set<IReadOnlyList<WorldEventCheckGroup>>(nameof(GameDataStore.WorldEventCheckGroup), t.Checks);
        Set<IReadOnlyList<WorldEventEventGroup>>(nameof(GameDataStore.WorldEventEventGroup), t.Groups);
        return store;
    }

    [Fact]
    public async Task OfferingWhoseSaveFailedIsStoredByTheNextTimerPass()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var repo = new FlakyRepo();
        var svc = new WorldEventService(StoreWithWorldEventTables(), players,
            new BroadcastService(players, NullLogger<BroadcastService>.Instance), repo, NullLogger<WorldEventService>.Instance);
        await svc.BuildWorldEntryBurstAsync(1100, default); // loads the states while the store is up

        repo.Fail = true;
        foreach (var item in new uint[] { 35000002, 35000001, 35000002, 35000001 }) // 100 of each completes stage 2 -> 3
            await svc.OnOfferingAsync(32510000, item, 50, default);
        Assert.DoesNotContain(repo.Saved, d => d.Id == Jawaii);

        repo.Fail = false;
        await svc.RunTimeoutsAsync(default);

        Assert.Equal((byte)3, Assert.Single(repo.Saved, d => d.Id == Jawaii).Stage);
    }
}
