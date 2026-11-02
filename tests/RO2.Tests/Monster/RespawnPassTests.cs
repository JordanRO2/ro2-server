using RO2.Monster.Application;
using Xunit;

namespace RO2.Tests.Monster;

/// <summary>The respawn selection (populated maps, 16 per tick, lazy order) and the announce gate.</summary>
public class RespawnPassTests
{
    private static IEnumerable<(uint Oid, uint Map)> Ready(int n, uint map = 1100)
        => Enumerable.Range(1, n).Select(i => ((uint)i, map));

    [Fact]
    public void NoPopulatedMapSelectsNothingAndDoesNotEnumerate()
    {
        var enumerated = false;
        IEnumerable<(uint, uint)> Source() { enumerated = true; yield return (1, 1100); }

        Assert.Empty(RespawnPass.Select(Source(), Array.Empty<uint>(), m => m.Item2));
        Assert.False(enumerated);
    }

    [Fact]
    public void OnlyMonstersOnPopulatedMapsAreSelectedInSourceOrder()
    {
        var ready = new (uint Oid, uint Map)[] { (1, 1100), (2, 2200), (3, 1100), (4, 3300) };

        var picked = RespawnPass.Select(ready, new uint[] { 1100, 3300 }, m => m.Map);

        Assert.Equal(new uint[] { 1, 3, 4 }, picked.Select(m => m.Oid));
    }

    [Fact]
    public void CapsAtSixteenPerTickKeepingTheFirstOnes()
    {
        var picked = RespawnPass.Select(Ready(40), new uint[] { 1100 }, m => m.Map);

        Assert.Equal(16, RespawnPass.MaxPerTick);
        Assert.Equal(16, picked.Length);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (uint)i), picked.Select(m => m.Oid));
    }

    [Fact]
    public void CapCountsOnlyMonstersOnPopulatedMaps()
    {
        var ready = Ready(20, 2200).Concat(Ready(20, 1100));

        var picked = RespawnPass.Select(ready, new uint[] { 1100 }, m => m.Map);

        Assert.Equal(16, picked.Length);
        Assert.All(picked, m => Assert.Equal(1100u, m.Map));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AnnouncesOnlyWithSpawnAndNpcInfoRow(bool spawn, bool row, bool expected)
        => Assert.Equal(expected, RespawnPass.MayAnnounce(spawn, row));
}
