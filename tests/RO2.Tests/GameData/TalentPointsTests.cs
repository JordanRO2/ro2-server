using RO2.Domain.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.GameData;

/// <summary>
/// Tests the talent point helpers against the shipped talent table and the values observed from the retail client.
/// </summary>
/// <remarks>
/// Covers the skill reset, the talent spend, and the pool sent at login. One known divergence, a point source the server does not model, is tested through the spend path instead.
/// </remarks>
public class TalentPointsTests
{
    [Theory]
    [InlineData(4, new uint[] { 4101, 4103, 4108 })]
    [InlineData(6, new uint[] { 1102, 1110, 1144 })]
    [InlineData(10, new uint[] { 3104, 3107, 3124 })]
    [InlineData(17, new uint[] { 6101, 6104, 6111 })]
    [InlineData(18, new uint[] { 6301, 6309, 6317 })]
    public async Task StarterSkillGroups_AreTheSetRetailResetsAndJobChangesLeave(int job, uint[] expected)
    {
        
        
        var store = await LoadShippedTalentTableAsync();

        Assert.Equal(expected, StatService.StarterSkillGroups(store, (byte)job));
    }

    [Theory]
    [InlineData(4, 43, new uint[] { 410101, 410301, 410801 }, 42)]            
    [InlineData(4, 50, new uint[] { 410101, 410301, 410801 }, 49)]            
    [InlineData(18, 50, new uint[] { 630101, 630901, 631701 }, 49)]           
    [InlineData(10, 25, new uint[] { 310401, 310701, 312401, 5030001 }, 24)]  
    public async Task TalentPointsRemaining_AfterAReset_IsWhatRetails2544Carried(
        int job, int level, uint[] wireSkillIds, int retailPool)
    {
        
        
        var store = await LoadShippedTalentTableAsync();

        Assert.Equal(retailPool, StatService.TalentPointsRemaining(store, (byte)job, level, FromWire(wireSkillIds)));
    }

    [Fact]
    public async Task TalentPointsSpent_OnRetailsPostReset1310State_IsThe69PointsItCost()
    {
        
        
        
        
        
        
        var store = await LoadShippedTalentTableAsync();
        var learned = FromWire([110201, 111001, 114401, 5030003]).ToDictionary(s => s.Group, s => s.Level);
        (uint Group, byte Level)[] vector1310 =
        [
            (1117, 5), (1108, 1), (1104, 5), (1107, 2), (1204, 5), (1201, 2), (1202, 10),
            (1207, 10), (1206, 10), (1216, 5), (1203, 5), (1200, 3), (1205, 5), (1102, 2),
        ];
        foreach (var (group, level) in vector1310)
            learned[group] = level;

        Assert.Equal(69, StatService.TalentPointsSpent(store, 6, learned.Select(kv => (kv.Key, kv.Value))));
    }

 /// <summary>Converts 2488 skill ids, each a group times 100 plus a level, to group and level pairs.</summary>
    private static (uint Group, byte Level)[] FromWire(uint[] wireSkillIds)
        => wireSkillIds.Select(id => (id / 100, (byte)(id % 100))).ToArray();

    private static async Task<GameDataStore> LoadShippedTalentTableAsync()
    {
        
        
        var source = Path.Combine(RequireDataDirectory(), "Talent_Table.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-talent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(source, Path.Combine(dir, "Talent_Table.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

 /// <summary>
 /// Returns the repository's data directory. Fails the test when it is missing, because the table is tracked.
 /// </summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Talent_Table.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Talent_Table.json");
    }
}
