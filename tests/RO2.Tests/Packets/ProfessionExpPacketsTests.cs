using System.Text.Json;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for 2526 OnNotifyProJobExpChange and 2527 OnNotifyProJobLevelUpPC, and the job-level rule behind them.
/// The level sums run on the shipped BaseExpTable data.
/// </summary>
public class ProfessionExpPacketsTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void ExpChange2526_MatchesRetailFrame()
    {
        
        Assert.Equal("011818001be4000000008f380000000000002a00000000000000",
            Hex(ProfessionExpPackets.BuildProJobExpChange2526(14479, 42)));
    }

    [Fact]
    public void LevelCrossing_MatchesRetailPair()
    {
        
        Assert.Equal("011818001be4000000000e000000000000002a00000000000000",
            Hex(ProfessionExpPackets.BuildProJobExpChange2526(14, 42)));
        
        Assert.Equal("010d0d001de40000000011d21a0310",
            Hex(ProfessionExpPackets.BuildProJobLevelUp2527(0x031AD211, 16)));
    }

    [Fact]
    public async Task Apply_ReproducesTheDumpsLevelCrossings()
    {
        var table = await LoadExpTableAsync();

        
        Assert.Equal(new ProfessionExp.Result(16, 14, true), ProfessionExp.Apply(15, 23026, 42, table));
        
        Assert.Equal(new ProfessionExp.Result(3, 573, true), ProfessionExp.Apply(1, 50, 887, table));
        
        Assert.Equal(new ProfessionExp.Result(5, 1079, true), ProfessionExp.Apply(3, 613, 2448, table));
        
        Assert.Equal(new ProfessionExp.Result(50, 0, true), ProfessionExp.Apply(49, 422057, 6811, table));
        
        Assert.Equal(new ProfessionExp.Result(15, 23026, false), ProfessionExp.Apply(15, 22984, 42, table));
    }

    [Fact]
    public async Task ExpToReach_EndsExactlyAtTheTarget()
    {
        var table = await LoadExpTableAsync();

        ulong toThirty = ProfessionExp.ExpToReach(1, 0, 30, table);
        Assert.Equal(new ProfessionExp.Result(30, 0, true), ProfessionExp.Apply(1, 0, toThirty, table));

        ulong toFifty = ProfessionExp.ExpToReach(12, 500, 50, table);
        Assert.Equal(new ProfessionExp.Result(50, 0, true), ProfessionExp.Apply(12, 500, toFifty, table));

        Assert.Equal(0UL, ProfessionExp.ExpToReach(30, 0, 30, table));   
        Assert.Equal(0UL, ProfessionExp.ExpToReach(40, 0, 30, table));
    }

 /// <summary>
 /// Job thresholds are read from Base_Exp_Next, because GameDataStore does not load Pro_Exp_Next. This holds only while
 /// the two columns are equal, which this test checks together with the level cap of 50.
 /// </summary>
    [Fact]
    public async Task ProExpNext_EqualsBaseExpNext_OnEveryRow()
    {
        await using var file = File.OpenRead(Path.Combine(DataDirectory(), "BaseExpTable.json"));
        using var doc = await JsonDocument.ParseAsync(file);
        int rows = 0;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(row.GetProperty("Base_Exp_Next").GetInt64(), row.GetProperty("Pro_Exp_Next").GetInt64());
            rows++;
        }
        Assert.Equal(50, rows);
        Assert.Equal(50, ProfessionExp.MaxLevel(await LoadExpTableAsync()));
    }

    private static async Task<Dictionary<byte, ExpTableEntry>> LoadExpTableAsync()
    {
        await using var file = File.OpenRead(Path.Combine(DataDirectory(), "BaseExpTable.json"));
        var rows = await JsonSerializer.DeserializeAsync<List<ExpTableEntry>>(file) ?? [];
        return rows.ToDictionary(r => r.Base_Level);
    }

    private static string DataDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "BaseExpTable.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/BaseExpTable.json");
    }
}
