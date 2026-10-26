using System.Collections.Generic;
using RO2.GameData;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="SpawnRules"/> (the spawn resolution moved out of WorldEntryService in B5 S10).</summary>
public class SpawnRulesTests
{
    private sealed class FakeCatalog : IWorldCatalog
    {
        private readonly Dictionary<uint, MapData> _maps = new();
        public FakeCatalog Add(uint id, float x, float y, float z) { _maps[id] = new MapData { Map_Index = id, x = x, y = y, z = z }; return this; }
        public MapData? GetMap(uint mapIndex) => _maps.TryGetValue(mapIndex, out var m) ? m : null;
    }

    private static readonly (uint, float, float, float) Default =
        (1101u, 209.34f, 268.79f, 48.23f);

    [Fact]
    public void DefaultSpawn_IsPinned() // data-bearing constants: the new-character spawn
    {
        Assert.Equal(1101u, SpawnRules.DefaultMapId);
        Assert.Equal(209.34f, SpawnRules.DefaultX);
        Assert.Equal(268.79f, SpawnRules.DefaultY);
        Assert.Equal(48.23f, SpawnRules.DefaultZ);
    }

    [Fact]
    public void MapZero_FallsBackToDefault()
        => Assert.Equal(Default, SpawnRules.ResolveSpawnPosition(0, 5f, 6f, 7f, new FakeCatalog().Add(0, 1, 1, 1)));

    [Fact]
    public void UnknownMap_FallsBackToDefault()
        => Assert.Equal(Default, SpawnRules.ResolveSpawnPosition(9999, 5f, 6f, 7f, new FakeCatalog().Add(1100, 1, 2, 3)));

    [Fact]
    public void NoCatalog_FallsBackToDefault()
        => Assert.Equal(Default, SpawnRules.ResolveSpawnPosition(1100, 5f, 6f, 7f, null));

    [Fact]
    public void KnownMap_KeepsTheStoredPosition()
        => Assert.Equal((1100u, 5f, 6f, 7f), SpawnRules.ResolveSpawnPosition(1100, 5f, 6f, 7f, new FakeCatalog().Add(1100, 1, 2, 3)));

    [Fact]
    public void KnownMap_ZeroPosition_TakesTheMapRowPosition()
        => Assert.Equal((1100u, 1f, 2f, 3f), SpawnRules.ResolveSpawnPosition(1100, 0f, 0f, 0f, new FakeCatalog().Add(1100, 1, 2, 3)));

    [Fact]
    public void KnownMap_ZeroPosition_MapRowPartlyZero_StillTakesIt()
        => Assert.Equal((1100u, 0f, 0f, 3f), SpawnRules.ResolveSpawnPosition(1100, 0f, 0f, 0f, new FakeCatalog().Add(1100, 0, 0, 3)));

    [Fact]
    public void KnownMap_ZeroPosition_ZeroMapRow_StaysAtZero()
        => Assert.Equal((1100u, 0f, 0f, 0f), SpawnRules.ResolveSpawnPosition(1100, 0f, 0f, 0f, new FakeCatalog().Add(1100, 0, 0, 0)));

    [Fact]
    public void KnownMap_PartialZeroStoredPosition_IsKept()
        => Assert.Equal((1100u, 0f, 0f, 9f), SpawnRules.ResolveSpawnPosition(1100, 0f, 0f, 9f, new FakeCatalog().Add(1100, 1, 2, 3)));
}
