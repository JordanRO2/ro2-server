using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The Domain aggression and vitals catalog rules, against in-memory fakes of their ports.</summary>
public class MonsterCatalogRulesTests
{
    private sealed class FakeAggression(uint asset, NpcAggressionMode mode, int gap) : INpcAggressionTable
    {
        public bool TryGet(uint a, out NpcAggressionMode m, out int g)
        {
            (m, g) = a == asset ? (mode, gap) : (NpcAggressionMode.None, 0);
            return a == asset;
        }
    }

    private sealed class FakeVitals(Dictionary<(uint, int), (int, int)> rows, Dictionary<int, int> byLevel) : IMonsterVitalsCatalog
    {
        public bool TryGet(uint asset, int level, out int maxHp, out int maxSp)
        {
            var found = rows.TryGetValue((asset, level), out var v);
            (maxHp, maxSp) = v;
            return found;
        }

        public bool TryGetLevelMaxSp(int level, out int maxSp) => byLevel.TryGetValue(level, out maxSp);
    }

    [Theory]
    [InlineData(NpcAggressionMode.None, 999, false)]
    [InlineData(NpcAggressionMode.Always, 0, true)]
    [InlineData(NpcAggressionMode.ByLevel, 10, true)]  // gap 10 exactly: playerLevel 60 - npc 50
    public void Acquires_PerMode(NpcAggressionMode mode, int gap, bool expected) =>
        Assert.Equal(expected, NpcAggressionRules.Acquires(mode, gap, npcLevel: 50, playerLevel: mode == NpcAggressionMode.ByLevel ? 60 : 1));

    [Fact]
    public void Acquires_ByLevel_RefusesOnePastTheGap() =>
        Assert.False(NpcAggressionRules.Acquires(NpcAggressionMode.ByLevel, 10, npcLevel: 50, playerLevel: 61));

    [Fact]
    public void Resolve_OnlyAMonsterTakesTheTablesRule()
    {
        var table = new FakeAggression(7, NpcAggressionMode.ByLevel, 5);
        Assert.Equal((NpcAggressionMode.ByLevel, 5), NpcAggressionRules.Resolve(table, camp: 2, asset: 7));
        Assert.Equal((NpcAggressionMode.None, 0), NpcAggressionRules.Resolve(table, camp: 1, asset: 7));   // friendly
        Assert.Equal((NpcAggressionMode.None, 0), NpcAggressionRules.Resolve(table, camp: 2, asset: 8));   // no rule
        Assert.Equal((NpcAggressionMode.None, 0), NpcAggressionRules.Resolve(null, camp: 2, asset: 7));    // no table
    }

    [Fact]
    public void ResolveVitals_PairThenLevelSpThenClampedMirror()
    {
        var catalog = new FakeVitals(new() { [(1u, 10)] = (551, 693) }, new() { [10] = 693, [20] = 0 });
        Assert.Equal((551, 693), MonsterVitalsRules.Resolve(catalog, 1, 10, 563));
        Assert.Equal((563, 693), MonsterVitalsRules.Resolve(catalog, 2, 10, 563));       // level SP
        Assert.Equal((563, 563), MonsterVitalsRules.Resolve(catalog, 2, 20, 563));       // level SP 0 is ignored
        Assert.Equal((120000, 65535), MonsterVitalsRules.Resolve(catalog, 2, 99, 120000)); // clamp to 16 bits
        Assert.Equal((563, 563), MonsterVitalsRules.Resolve(null, 1, 10, 563));
    }
}
