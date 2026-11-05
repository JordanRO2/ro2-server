using RO2.Characters.Domain;
using RO2.GameData;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>Master-level rules: who may convert, the per-rank level cap, and the table-driven level-up cost.</summary>
[Collection(GameDataStoreLoadedCollection.Name)]
public class MasteryRulesTests
{
    [Theory]
    [InlineData(50, 0, true)]   // at the cap, not started -> may start
    [InlineData(51, 0, true)]   // above the cap counts too
    [InlineData(49, 0, false)]  // below the base cap
    [InlineData(50, 1, false)]  // already started
    [InlineData(50, 7, false)]  // already well into mastery
    public void CanStart_RequiresBaseLevelCapAndNotStarted(int baseLevel, int masteryLevel, bool expected)
        => Assert.Equal(expected, MasteryRules.CanStart((byte)baseLevel, (byte)masteryLevel));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 30)]
    [InlineData(3, 40)]
    [InlineData(4, 50)]
    public void LevelCapForRank_IsTenPerRank(int rank, int cap)
        => Assert.Equal(cap, MasteryRules.LevelCapForRank((byte)rank));

    [Theory]
    [InlineData(50, 0, 0, 75)]      // base pool at level 50, no mastery
    [InlineData(50, 7, 0, 110)]     // + 7 master levels x 5
    [InlineData(1, 2, 0, 10)]       // base 0 + 2 master levels x 5
    [InlineData(50, 40, 100, 375)]  // level 50 + full mastery (200) + bought (100) = the 375 max
    public void TotalBonusStatPoints_AddsMasteryAndBought(int level, int masteryLevel, int bought, int expected)
        => Assert.Equal(expected, CharacterStats.TotalBonusStatPointsEarned(level, masteryLevel, bought));

    [DataFact]
    public async Task RankUpRequirement_IsTheTargetGradeRow_NullAtTopRankOrUnknownClass()
    {
        var store = await LoadMasteryTablesAsync();

        // Rank 0 (D) advances to grade 2 (C): level 10, 10,000 points, 3,000 coin, and a per-class quest.
        var req = MasteryRules.RankUpRequirement(store, 0, 6);
        Assert.NotNull(req);
        Assert.Equal(((byte)10, 10000u, 3000u), (req!.Value.NeedLevel, req.Value.NeedPoint, req.Value.NeedCoin));
        Assert.NotEqual(0u, req.Value.NeedQuestId);
        // Top rank (S = 4) cannot advance; an unknown class has no row.
        Assert.Null(MasteryRules.RankUpRequirement(store, 4, 6));
        Assert.Null(MasteryRules.RankUpRequirement(store, 0, 99));
    }

    [DataFact]
    public async Task LevelUpCost_IsTheNextLevelsRow_AndNullAtTheRankCap()
    {
        var store = await LoadMasteryTablesAsync();

        // Level 1 -> 2 (D rank): the level-2 row, 1000 points / 500 coin.
        Assert.Equal((1000u, 500u), MasteryRules.LevelUpCost(store, 1, 0));
        // Level 30 -> 31 at rank A: the level-31 row, 43,400 points / 31,100 coin (the in-game example).
        Assert.Equal((43400u, 31100u), MasteryRules.LevelUpCost(store, 30, 3));
        // At a rank's cap there is no level-up: rank D caps at 10, rank B at 30.
        Assert.Null(MasteryRules.LevelUpCost(store, 10, 0));
        Assert.Null(MasteryRules.LevelUpCost(store, 30, 2));
        // Not started yet.
        Assert.Null(MasteryRules.LevelUpCost(store, 0, 0));
    }

    private static async Task<GameDataStore> LoadMasteryTablesAsync()
    {
        var dataDir = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-mastery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(dataDir, "MasteryTable.json"), Path.Combine(dir, "MasteryTable.json"));
            File.Copy(Path.Combine(dataDir, "MasteryGradeTable.json"), Path.Combine(dir, "MasteryGradeTable.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "MasteryTable.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/MasteryTable.json");
    }
}
