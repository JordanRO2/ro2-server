using System.Collections.Generic;
using RO2.GameData;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="WarpDestinationRules"/> (the warp-destination lookups gathered in World.Domain in B5 S11).</summary>
public class WarpDestinationRulesTests
{
    private sealed class FakeCatalog : IWorldCatalog
    {
        private readonly Dictionary<uint, MapData> _maps = new();
        private readonly Dictionary<uint, PositionData> _positions = new();
        private readonly Dictionary<uint, NpcData> _npcs = new();

        public FakeCatalog Map(uint id, byte type = 0, uint mapId = 0, float x = 0, float y = 0, float z = 0)
        {
            _maps[id] = new MapData { Map_Index = id, Map_Type = type, Map_ID = mapId, x = x, y = y, z = z };
            return this;
        }

        public FakeCatalog Position(uint id, uint area, float x, float y, float z)
        {
            _positions[id] = new PositionData { Position_ID = id, Area_ID = area, Position_X = x, Position_Y = y, Position_Z = z };
            return this;
        }

        public FakeCatalog Npc(uint id, (int Type, uint Id) f1, (int Type, uint Id) f2 = default, (int Type, uint Id) f3 = default)
        {
            _npcs[id] = new NpcData
            {
                ID = id,
                Function_Type_1 = f1.Type, Function_ID_1 = f1.Id,
                Function_Type_2 = f2.Type, Function_ID_2 = f2.Id,
                Function_Type_3 = f3.Type, Function_ID_3 = f3.Id,
            };
            return this;
        }

        public MapData? GetMap(uint mapIndex) => _maps.GetValueOrDefault(mapIndex);
        public PositionData? GetPosition(uint positionId) => _positions.GetValueOrDefault(positionId);
        public NpcData? GetNpc(uint npcId) => _npcs.GetValueOrDefault(npcId);
    }

    [Fact]
    public void FlaggedIds_ArePinned() // hard-coded game ids carried over unchanged
    {
        Assert.Equal(9100101u, WarpDestinationRules.ButterflyWing);
        Assert.Equal(9110001u, WarpDestinationRules.DungeonWing);
        Assert.Equal(9100201u, WarpDestinationRules.PronteraWarp);
        Assert.Equal(9100301u, WarpDestinationRules.AlbertaWarp);
        Assert.Equal(9100701u, WarpDestinationRules.MorrocWarp);
        Assert.Equal(9100801u, WarpDestinationRules.TrainingGroundWarp);
        Assert.Equal(9101001u, WarpDestinationRules.JawaiiWarp);
        Assert.Equal(294u, WarpDestinationRules.TrainingGroundPosition);
        Assert.Equal(410u, WarpDestinationRules.JawaiiPosition);
    }

    [Fact]
    public void TownWarp_FixedAndPositionInfoDestinations_NeedTheirMap()
    {
        var c = new FakeCatalog().Map(1100).Map(1200).Map(1300).Map(1111).Position(294, 1111, 1112f, 889f, 85f);
        Assert.Equal(new WarpDestination(1100, 798.12f, 767.1f, 57.18f), WarpDestinationRules.TownWarpDestination(9100201, c));
        Assert.Equal(new WarpDestination(1200, 722.76f, 547.03f, 55.32f), WarpDestinationRules.TownWarpDestination(9100301, c));
        Assert.Equal(new WarpDestination(1300, 434f, 249f, 12f), WarpDestinationRules.TownWarpDestination(9100701, c));
        Assert.Equal(new WarpDestination(1111, 1112f, 889f, 85f), WarpDestinationRules.TownWarpDestination(9100801, c));
        Assert.Null(WarpDestinationRules.TownWarpDestination(9101001, c));            // Position 410 not loaded
        Assert.Null(WarpDestinationRules.TownWarpDestination(9100401, c));            // not a town warp
        Assert.Null(WarpDestinationRules.TownWarpDestination(9100201, new FakeCatalog())); // map 1100 not loaded
    }

    [Fact]
    public void DungeonWing_ReturnPoint_RefusedOnFieldMapZeroIdOrMissingMap()
    {
        var c = new FakeCatalog().Map(1101);
        var inst = new MapData { Map_Type = 1, Map_ID = 1101, x = 1538f, y = 664f, z = 12f };
        Assert.Equal(new WarpDestination(1101, 1538f, 664f, 12f), WarpDestinationRules.DungeonWingDestination(inst, c));
        Assert.Null(WarpDestinationRules.DungeonWingDestination(inst with { Map_Type = 0 }, c));
        Assert.Null(WarpDestinationRules.DungeonWingDestination(inst with { Map_ID = 0 }, c));
        Assert.Null(WarpDestinationRules.DungeonWingDestination(inst, new FakeCatalog()));
        Assert.Null(WarpDestinationRules.DungeonWingDestination(null, c));
    }

    [Fact]
    public void ButterflyWing_BindThenSpawn()
    {
        var c = new FakeCatalog().Map(1200).Position(103, 1200, 728f, 537f, 57f).Position(104, 9999, 1f, 2f, 3f);
        Assert.Equal(new WarpDestination(1200, 728f, 537f, 57f), WarpDestinationRules.ButterflyWingDestination(103, c));
        var spawn = new WarpDestination(SpawnRules.DefaultMapId, SpawnRules.DefaultX, SpawnRules.DefaultY, SpawnRules.DefaultZ);
        Assert.Equal(spawn, WarpDestinationRules.ButterflyWingDestination(0, c));
        Assert.Equal(spawn, WarpDestinationRules.ButterflyWingDestination(104, c)); // bind map not loaded
        Assert.Equal(spawn, WarpDestinationRules.ButterflyWingDestination(105, c)); // unknown bind
    }

    [Fact]
    public void AnswerDestination_RoutesBySkill()
    {
        var c = new FakeCatalog().Map(1200).Map(1101).Map(5000, type: 1, mapId: 1101, x: 7f, y: 8f, z: 9f)
            .Position(103, 1200, 728f, 537f, 57f);
        Assert.Equal(new WarpDestination(1200, 728f, 537f, 57f), WarpDestinationRules.AnswerDestination(9100101, 103, 5000, c));
        Assert.Equal(new WarpDestination(1101, 7f, 8f, 9f), WarpDestinationRules.AnswerDestination(9110001, 103, 5000, c));
        Assert.Null(WarpDestinationRules.AnswerDestination(9110001, 103, 1101, c)); // field map
        Assert.Null(WarpDestinationRules.AnswerDestination(9100201, 103, 5000, c)); // a town warp moves on landing
    }

    [Fact]
    public void IsInPlace_SameMapAndNotInstance()
    {
        var c = new FakeCatalog().Map(1100, type: 0).Map(5000, type: 1);
        Assert.True(WarpDestinationRules.IsInPlace(1100, 1100, c));
        Assert.False(WarpDestinationRules.IsInPlace(1100, 1200, c));
        Assert.False(WarpDestinationRules.IsInPlace(5000, 5000, c));
        Assert.True(WarpDestinationRules.IsInPlace(7777, 7777, c)); // current map unknown: not an instance
    }

    [Fact]
    public void FunctionId_FirstSlotOfTheType()
    {
        var c = new FakeCatalog().Npc(1, (5, 50u), (12, 120u), (12, 121u));
        Assert.True(WarpDestinationRules.TryResolveFunctionId(c, 1, 12, out var id));
        Assert.Equal(120u, id);
        Assert.False(WarpDestinationRules.TryResolveFunctionId(c, 1, 20, out id));
        Assert.Equal(0u, id);
        Assert.False(WarpDestinationRules.TryResolveFunctionId(c, 2, 12, out _));
    }

    [Fact]
    public void FunctionPosition_SkipsASlotWithoutARow()
    {
        var c = new FakeCatalog().Npc(1, (12, 120u), (12, 121u)).Position(121, 1100, 1f, 2f, 3f);
        Assert.True(WarpDestinationRules.TryResolveFunctionPosition(c, 1, 12, out var p));
        Assert.Equal(121u, p.Position_ID);
        Assert.False(WarpDestinationRules.TryResolveFunctionPosition(c, 1, 20, out _));
        Assert.False(WarpDestinationRules.TryResolveFunctionPosition(c, 9, 12, out _));
    }

    [Theory]
    [InlineData(1, 274u)]
    [InlineData(4, 327u)]
    [InlineData(0, 0u)]
    [InlineData(5, 0u)]
    public void MultiTeleport_SlotIsOneBased(int slot, uint expected)
    {
        var c = new FakeCatalog().Npc(1, (39, 1u)).Map(1100);
        foreach (var id in new uint[] { 274, 325, 326, 327 }) c.Position(id, 1100, id, 0f, 0f);
        Assert.Equal(expected != 0, WarpDestinationRules.TryResolveMultiTeleportDestination(c, 1, (byte)slot, out var d));
        if (expected != 0) Assert.Equal(expected, d.Position_ID);
    }

    [Fact]
    public void MultiTeleport_RefusesUnknownGroupMissingRowOrMissingMap()
    {
        var c = new FakeCatalog().Npc(1, (39, 4u)).Npc(2, (39, 1u)).Npc(3, (12, 1u)).Position(274, 3101, 0f, 0f, 0f);
        Assert.False(WarpDestinationRules.TryResolveMultiTeleportDestination(c, 1, 1, out _)); // group 4 is an agit, unserved
        Assert.False(WarpDestinationRules.TryResolveMultiTeleportDestination(c, 2, 1, out _)); // map 3101 not loaded
        Assert.False(WarpDestinationRules.TryResolveMultiTeleportDestination(c, 3, 1, out _)); // no type-39 function
        Assert.False(WarpDestinationRules.TryResolveMultiTeleportDestination(c, 2, 2, out _)); // Position 325 not loaded
    }

    [Fact]
    public void SavePoint_ReturnPointThenBindThenSpawn()
    {
        var c = new FakeCatalog().Map(3101, mapId: 1100, x: 610f, y: 969f, z: 43f).Map(1100).Map(1200)
            .Position(103, 1200, 728f, 537f, 57f);
        Assert.Equal((1100u, 610f, 969f, 43f, "Map_List 3101 return point"), WarpDestinationRules.ResolveSavePoint(3101, 103, c));
        Assert.Equal((1200u, 728f, 537f, 57f, "bind 103"), WarpDestinationRules.ResolveSavePoint(1100, 103, c));
        Assert.Equal((SpawnRules.DefaultMapId, SpawnRules.DefaultX, SpawnRules.DefaultY, SpawnRules.DefaultZ, "no bind: new-character spawn"),
            WarpDestinationRules.ResolveSavePoint(1100, 0, c));
        // A return point whose map is not loaded falls through to the bind.
        var d = new FakeCatalog().Map(3101, mapId: 1100).Map(1200).Position(103, 1200, 728f, 537f, 57f);
        Assert.Equal((1200u, 728f, 537f, 57f, "bind 103"), WarpDestinationRules.ResolveSavePoint(3101, 103, d));
    }
}
