using RO2.GameData.Tables;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure unit tests of the Guild.Domain vault capacity rules (no host).</summary>
public class GuildVaultRulesTests
{
    private static GuildStorageItem Cell(int slot) => new() { StorageSlot = (byte)slot };

    [Fact]
    public void MaxCells_ClampsToAddressableAndFallsBack()
    {
        Assert.Equal(40, GuildVaultRules.MaxCells([new ConfigDef { type = GuildVaultRules.ConfigMaxVaultCells, Value = "40" }]));
        Assert.Equal(256, GuildVaultRules.MaxCells([new ConfigDef { type = GuildVaultRules.ConfigMaxVaultCells, Value = "999" }]));
        Assert.Equal(256, GuildVaultRules.MaxCells([new ConfigDef { type = GuildVaultRules.ConfigMaxVaultCells, Value = "0" }]));
        Assert.Equal(256, GuildVaultRules.MaxCells([]));
    }

    [Fact]
    public void SlotCapacity_TakesHighestVaultCategoryValueCappedByConfig()
    {
        var table = new Dictionary<uint, GuildSkillLevel>
        {
            [1] = new() { Skill_Category_3 = GuildVaultRules.SkillCategoryVaultCells, Skill_Value_3 = 30 },
            [2] = new() { Skill_Category_1 = GuildVaultRules.SkillCategoryVaultCells, Skill_Value_1 = 70 },
        };
        Assert.Equal(70, GuildVaultRules.SlotCapacity([1, 2, 99], table, []));
        Assert.Equal(50, GuildVaultRules.SlotCapacity([2], table, [new ConfigDef { type = GuildVaultRules.ConfigMaxVaultCells, Value = "50" }]));
        Assert.Equal(0, GuildVaultRules.SlotCapacity([99], table, []));
    }

    [Fact]
    public void LowestFreeCell_SkipsUsedAndRespectsCapacity()
    {
        Assert.Equal(1, GuildVaultRules.LowestFreeCell([Cell(0), Cell(2)], 5));
        Assert.Equal(-1, GuildVaultRules.LowestFreeCell([Cell(0)], 1));
        Assert.Equal(-1, GuildVaultRules.LowestFreeCell([], -1));
    }
}
