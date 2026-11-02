using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Pure tests for the Monster.Domain aggro-acquisition rule (no host, no players, no clock).</summary>
public class AggroAcquisitionTests
{
    private sealed record Pc(string Name, float X, float Y, int Level = 10);

    private static Pc? Scan(MonsterAiState ai, params Pc[] pcs)
        => AggroAcquisition.Nearest(ai, 10, 0f, 0f, pcs, static p => new AggroCandidate(p.X, p.Y, p.Level));

    private static MonsterAiState Ai(float sight = 0f, NpcAggressionMode mode = NpcAggressionMode.Always, int gap = 0)
        => new() { SightRadius = sight, Aggression = mode, AggressionLevelGap = gap };

    [Fact]
    public void DefaultSightIs9_BoundaryInclusive()
    {
        Assert.Equal("in", Scan(Ai(), new Pc("in", 9f, 0f))!.Name);
        Assert.Null(Scan(Ai(), new Pc("out", 9.01f, 0f)));
    }

    [Fact]
    public void ExplicitSight_ReplacesTheDefault()
    {
        Assert.Null(Scan(Ai(5f), new Pc("far", 6f, 0f)));
        Assert.Equal("near", Scan(Ai(5f), new Pc("near", 3f, 4f))!.Name);
    }

    [Fact]
    public void Nearest_BySquaredDistance()
        => Assert.Equal("b", Scan(Ai(), new Pc("a", 5f, 0f), new Pc("b", 2f, 2f), new Pc("c", 6f, 0f))!.Name);

    [Fact]
    public void EqualDistance_LastScannedWins()
        => Assert.Equal("second", Scan(Ai(), new Pc("first", 3f, 0f), new Pc("second", 0f, 3f))!.Name);

    [Fact]
    public void NonFinite_IsRejected()
        => Assert.Equal("ok", Scan(Ai(), new Pc("nan", float.NaN, 0f), new Pc("inf", float.PositiveInfinity, 0f),
            new Pc("ok", 1f, 1f))!.Name);

    [Fact]
    public void Empty_IsNull() => Assert.Null(Scan(Ai()));

    [Fact]
    public void AggressionMode_GatesEachCandidate()
    {
        Assert.Null(Scan(Ai(mode: NpcAggressionMode.None), new Pc("p", 1f, 0f)));
        // ByLevel gap 2, npc level 10: level 13 is skipped, level 12 is taken even though farther.
        var ai = Ai(mode: NpcAggressionMode.ByLevel, gap: 2);
        Assert.Equal("far12", Scan(ai, new Pc("near13", 1f, 0f, 13), new Pc("far12", 5f, 0f, 12))!.Name);
    }
}
