using System.Collections.Generic;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="WarpUseCase"/> and the entry-point rule behind it (B5 S25).</summary>
public class WarpUseCaseTests
{
    private sealed class MapsOnly(params MapData[] maps) : IWorldCatalog
    {
        private readonly Dictionary<uint, MapData> _maps = maps.ToDictionary(m => m.Map_Index);
        public MapData? GetMap(uint mapIndex) => _maps.GetValueOrDefault(mapIndex);
        public PositionData? GetPosition(uint positionId) => null;
        public NpcData? GetNpc(uint npcId) => null;
    }

    private static MapData Map(uint id, float x = 0, float y = 0, float z = 0)
        => new() { Map_Index = id, x = x, y = y, z = z };

    [Fact]
    public void AnUnknownMap_DecidesNothing()
    {
        var catalog = new MapsOnly(Map(1100));
        Assert.Null(WarpUseCase.Decide(catalog, 9999, 1, 2, 3, 1100));
        Assert.False(WarpUseCase.CanCommitClientMapChange(catalog, 9999));
    }

    [Fact]
    public void AKnownMap_MayCommitAClientMapChange()
        => Assert.True(WarpUseCase.CanCommitClientMapChange(new MapsOnly(Map(1100)), 1100));

    [Fact]
    public void AnAllZeroRequest_TakesTheMapsDefaultEntryPoint()
    {
        var d = WarpUseCase.Decide(new MapsOnly(Map(1101, 10.5f, 20.5f, 30.5f)), 1101, 0f, 0f, 0f, 1100)!.Value;
        Assert.Equal((10.5f, 20.5f, 30.5f), (d.X, d.Y, d.Z));
    }

    [Fact]
    public void AnAllZeroRequest_StaysZeroWhenTheMapHasNoEntryPoint()
    {
        var d = WarpUseCase.Decide(new MapsOnly(Map(1101)), 1101, 0f, 0f, 0f, 1100)!.Value;
        Assert.Equal((0f, 0f, 0f), (d.X, d.Y, d.Z));
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(-5f, 6f, 7f)]
    public void ANonZeroRequest_IsKept(float x, float y, float z)
    {
        var d = WarpUseCase.Decide(new MapsOnly(Map(1101, 10f, 20f, 30f)), 1101, x, y, z, 1100)!.Value;
        Assert.Equal((x, y, z), (d.X, d.Y, d.Z));
    }

    [Fact]
    public void NegativeZero_CountsAsZero()
    {
        var d = WarpUseCase.Decide(new MapsOnly(Map(1101, 10f, 20f, 30f)), 1101, -0f, -0f, -0f, 1100)!.Value;
        Assert.Equal((10f, 20f, 30f), (d.X, d.Y, d.Z));
    }

    [Theory]
    [InlineData(1100u, 1101u, true)]
    [InlineData(1101u, 1101u, false)]
    public void ChangesMap_IsTheDestinationDifferingFromTheCurrentMap(uint current, uint dest, bool changes)
        => Assert.Equal(changes, WarpUseCase.Decide(new MapsOnly(Map(dest)), dest, 1, 1, 1, current)!.Value.ChangesMap);

    [Fact]
    public void TheMapLoadTail_IsTheWarpOrderWithoutTheCardBookRegistrations()
    {
        // 2311 map load, 2472 quick slots, 2523 + 2522 vehicles, 2913 monster book, 2919 pet book, 2918 active pet:
        // the sequence WarpService sent before it was driven by the plan.
        var d = WarpUseCase.Decide(new MapsOnly(Map(1101)), 1101, 1, 1, 1, 1100)!.Value;
        Assert.Equal(new ushort[] { 2311, 2472, 2523, 2522, 2913, 2919, 2918 },
            d.MapLoadSteps.Select(WorldEntryStepOpcodes.Of).ToArray());
    }

    [Fact]
    public void TheMapLoadTail_FollowsThePreMapLoadBurstOfTheWarpPlan()
    {
        var warp = WorldEntryPlan.For(WorldEntryMode.Warp).Steps;
        Assert.Equal(warp.Take(WorldEntryPlan.PreMapLoad.Count), WorldEntryPlan.PreMapLoad);
        Assert.Equal(warp.Skip(WorldEntryPlan.PreMapLoad.Count).Where(s => s != WorldEntryStep.RegisterCardBook),
            WarpUseCase.MapLoadSteps);
    }

    [Fact]
    public void TheEmptyQuickSlotBar_IsTheLiteralBodyWarpServiceWroteBeforeTheCarve()
    {
        const ulong charUid = 0x0102030405060708UL;
        using var qs = new PacketWriter();
        qs.WriteUInt32(10);
        qs.WriteUInt64(charUid);
        qs.WriteUInt16(0);
        Assert.Equal(qs.GetWrittenMemory().ToArray(),
            GuildSkillBarRefresh.BuildQuickSlotSaveData2472(charUid, Array.Empty<CharacterQuickSlot>()));
    }
}
