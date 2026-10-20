using System.Text.Json;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Reference frames for the world-entry and map-change builders of WorldEntryService: 2535 OnNotifyCraftIndex, 2488 OnUseSkillList order,
/// 2669 NfyServerTimeToLoginPC, 2743 OnSendDependentDungeonInfo, and the bind byte of the shared 126-byte item record.
/// </summary>
/// <remarks>
/// The 2535 bodies are the four Craft_Level-1 lists from the reference frames. They are rebuilt here from data/Craft_ItemList.json,
/// read with System.Text.Json so that no GameDataStore is involved.
/// </remarks>
public class MapLoadBurstBuildersTests
{
    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes = new(() =>
        JsonSerializer.Deserialize<List<CraftItemList>>(File.ReadAllText(DataFile("Craft_ItemList.json")))!);

    [Theory]
    [InlineData(1, "060000000200b401b501")]
    [InlineData(2, "0c00000005005a015b015c015d015e01")]
    [InlineData(3, "080000000300010002000300")]
    [InlineData(4, "0e00000006008e008f009000910092009300")]
    public void CraftIndex2535_IsTheProfessionsLevel1Set_ByteForByte(byte proType, string retailHex)
    {
        var body = WorldEntryService.BuildNotifyCraftIndex2535(Recipes.Value, proType);
        Assert.NotNull(body);
        Assert.Equal(retailHex.ToUpperInvariant(), Convert.ToHexString(body!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void CraftIndex2535_NoRecipes_SendsNothing(byte proType)
    {
        
        Assert.Null(WorldEntryService.BuildNotifyCraftIndex2535(Recipes.Value, proType));
    }

    [Fact]
    public void UseSkillList2488_ListsTheIdsAscending_WhateverTheInputOrder()
    {
        
        
        var body = WorldEntryService.BuildUseSkillList2488(new (uint, byte)[] { (4103u, 1), (4108u, 1), (4101u, 1) });
        Assert.Equal("011818" + "00B3E3" + "00000000" + "03000000" + "F5410600" + "BD420600" + "B1440600",
            Convert.ToHexString(body));
    }

    [Fact]
    public void ServerTime2669_EncodesTheRetailInstant()
    {
        
        
        const ulong retailMicros = 212649800652731329UL;
        var body = WorldEntryService.BuildServerTimeToLoginPC2669(ServerTimeCodec.FromClientMicroseconds(retailMicros));
        Assert.Equal(8, body.Length);
        Assert.Equal(retailMicros / 1000UL, BitConverter.ToUInt64(body, 0) / 1000UL);
        Assert.Equal("76F5DD7BF302", Convert.ToHexString(body.AsSpan(2).ToArray()));
    }

    [Fact]
    public void DependentDungeonInfo2743_IsRetailsEmptyForm()
    {
        
        Assert.Equal("020000000000", Convert.ToHexString(WorldEntryService.BuildDependentDungeonInfo2743()));
    }

 /// <summary>
 /// The bind byte of four reference records, checked against the Bind_Type of the template read from data/ItemInfo.json.
 /// </summary>
    [Theory]
    
    [InlineData("8f1264c6a73d040e73d9fe0087200100013675000000000000000002001400000006224e25a9530106224e25a9530107e00625a9530100000000000000000000000000000007a100008800ac0ff0f874420df97242a500322321fca90fca0200000000000000000000000000000000000000000000000000000000000000", 1)]
    
    [InlineData("8d089509a83d040e96dbfe0010270100090000000000000000000002000000000006224e26a95301000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003e010000b24200000000000000000000000000000000000000000000000000", 1)]
    
    [InlineData("a6079509a83d040e97dbfe00102701050b0000000000000000000002000000000006224e918a930300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000030100007c4200000000000000000000000000000000000000000000000000", 2)]
    
    [InlineData("ea1c1508a83d040e972a2c0400000105000000000000000000000000000000000006224eca87930306224eca87930306224eca879303000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", 2)]
    public void BindFlags_MatchRetailRecords(string recordHex, byte storageType)
    {
        var record = Convert.FromHexString(recordHex);
        var templateId = BitConverter.ToUInt32(record, 8);
        var bindType = BindTypeOf(templateId);
        bool worn = record[0x0F] == 0;
        Assert.Equal(worn, storageType == 1);
        Assert.Equal(record[0x1B], WorldEntryService.RecordBindFlags(bindType, storageType));
    }

    [Fact]
    public void BindFlags_BindOnEquipInABag_StaysUnbound()
    {
        
        
        
        var record = Convert.FromHexString(
            "a43db9eda73d040e67dbfe00102701041c0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000023d5000060000c00c3fc8c3eaffdbb36a901bb3699febb3603ff00000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(1, BindTypeOf(BitConverter.ToUInt32(record, 8)));
        Assert.Equal(record[0x1B], WorldEntryService.RecordBindFlags(1, 0));
    }

    private static byte BindTypeOf(uint templateId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFile("ItemInfo.json")));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.GetProperty("ID").GetUInt32() == templateId)
                return row.GetProperty("Bind_Type").GetByte();
        throw new InvalidOperationException($"template {templateId} is not in data/ItemInfo.json");
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }
}
