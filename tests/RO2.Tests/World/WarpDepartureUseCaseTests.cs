using System.Collections.Generic;
using System.Linq;
using RO2.GameData.Tables;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="WarpDepartureUseCase"/> and <see cref="WarpEffectRules"/> (B5 S27).</summary>
public class WarpDepartureUseCaseTests
{
    private sealed record Fx(uint Id, int Level);

    private static bool EndsOn(uint id) => id is 60037 or 40013 or 60043;

    [Fact]
    public void WarpHandlingZeroIsTheOnlyValueThatEnds()
    {
        Assert.True(WarpEffectRules.EndsOnWarp(0));
        Assert.False(WarpEffectRules.EndsOnWarp(1));
        Assert.False(WarpEffectRules.EndsOnWarp(255));
    }

    [Fact]
    public void EndsOnWarpIdsAreTheWarpHandlingZeroRows()
    {
        var table = new[]
        {
            new EffectTable { ID = 10, Warp_Handling = 0 },
            new EffectTable { ID = 11, Warp_Handling = 1 },
            new EffectTable { ID = 12, Warp_Handling = 0 },
        };
        Assert.Equal(new uint[] { 10, 12 }, WarpEffectRules.EndsOnWarpIds(table).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void DroppedIdsKeepApplyOrderAndAppearOnce()
    {
        var active = new[] { new Fx(60043, 1), new Fx(40000, 1), new Fx(40013, 1), new Fx(60043, 2), new Fx(60037, 1) };
        Assert.Equal(new uint[] { 60043, 40013, 60037 },
            WarpDepartureUseCase.DroppedEffectIds(active, e => e.Id, EndsOn));
    }

    [Fact]
    public void KeptEffectsAreTheSameObjectsInOrderAndTheDropSetIsExcluded()
    {
        var a = new Fx(40000, 1);
        var b = new Fx(60043, 1);
        var c = new Fx(40009, 10);
        var kept = WarpDepartureUseCase.KeptEffects(new[] { a, b, c }, e => e.Id, EndsOn);
        Assert.Equal(2, kept.Count);
        Assert.Same(a, kept[0]);
        Assert.Same(c, kept[1]);
    }

    [Fact]
    public void NothingActiveDropsAndKeepsNothing()
    {
        Assert.Empty(WarpDepartureUseCase.DroppedEffectIds(new List<Fx>(), e => e.Id, EndsOn));
        Assert.Empty(WarpDepartureUseCase.KeptEffects(new List<Fx>(), e => e.Id, EndsOn));
    }

    [Fact]
    public void AKeptEffectSendsARunOnlyWithTraitLines()
    {
        Assert.False(WarpDepartureUseCase.SendsKeptRun(0));
        Assert.True(WarpDepartureUseCase.SendsKeptRun(1));
        Assert.True(WarpDepartureUseCase.SendsKeptRun(3));
    }

    [Fact]
    public void HasKeptRunStopsAtTheFirstEffectWithLines()
    {
        var asked = new List<uint>();
        var kept = new[] { new Fx(1, 1), new Fx(2, 1), new Fx(3, 1) };
        Assert.True(WarpDepartureUseCase.HasKeptRun(kept, e => { asked.Add(e.Id); return e.Id == 2 ? 2 : 0; }));
        Assert.Equal(new uint[] { 1, 2 }, asked);
        Assert.False(WarpDepartureUseCase.HasKeptRun(kept, _ => 0));
        Assert.False(WarpDepartureUseCase.HasKeptRun(new List<Fx>(), _ => 5));
    }

    [Fact]
    public void TheDepartureIsTheMarkerThenTheSelfDespawn()
        => Assert.Equal(new[] { WorldEntryStep.DepartureMarker, WorldEntryStep.SelfDespawn },
            WarpDepartureUseCase.DepartureSteps);
}
