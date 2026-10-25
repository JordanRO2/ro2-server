using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Refusal tests for RMI 1543 ReqAttachSocket (<see cref="AttachSocketHandler.Check"/>, <see cref="AttachSocketHandler.BuildRefusal2786"/>).
/// The 2786 result code selects the message shown by the client: 1 unable to find the rune item, 2 unable to find
/// the target item, 3 not a rune item, 4 wrong socket slot index, 5 rune not valid for the socket type,
/// 6 unable to equip to the target item. Fixtures are rows from the shipped data.
/// </summary>
public class AttachSocket1543Tests
{
    private const uint Owner = 7;

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-attachsocket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":22261001,"Item_Type":29},{"ID":22262001,"Item_Type":29},{"ID":22290036,"Item_Type":32},
                 {"ID":22290033,"Item_Type":32},{"ID":12611000,"Item_Type":2,"Equip_Slot":2,"Require_Level":0},
                 {"ID":22261100,"Item_Type":49,"RandomSet_ID":1100},{"ID":22261200,"Item_Type":49,"RandomSet_ID":1200},
                 {"ID":22261101,"Item_Type":29},{"ID":22261102,"Item_Type":29},{"ID":22261103,"Item_Type":29},
                 {"ID":22310892,"Item_Type":13},{"ID":11100001,"Item_Type":1,"Socket_GroupID":1,"Equip_Slot":6,"Require_Level":50},
                 {"ID":11100029,"Item_Type":1,"Socket_GroupID":11,"Equip_Slot":6,"Require_Level":50}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemReinforceInfo.json"), """
                [{"Reinforce_ID":1001,"Reinforce_Type":0,"SourceItem_ID":22261001,"TargetItem_Socket_Type":1},
                 {"Reinforce_ID":2001,"Reinforce_Type":0,"SourceItem_ID":22262001,"TargetItem_Socket_Type":2},
                 {"Reinforce_ID":30006,"Reinforce_Type":2,"SourceItem_ID":22290036,"TargetItem_Socket_Type":0,"Duration":300,"TargetItem_ReqLevel":1,"TargetItem_Parts":100663358},
                 {"Reinforce_ID":30003,"Reinforce_Type":2,"SourceItem_ID":22290033,"TargetItem_Socket_Type":0,"Duration":300,"TargetItem_ReqLevel":1,"TargetItem_Parts":100663488},
                 {"Reinforce_ID":1100,"Reinforce_Type":0,"SourceItem_ID":22261100,"TargetItem_Socket_Type":2},
                 {"Reinforce_ID":1101,"Reinforce_Type":0,"SourceItem_ID":22261101,"TargetItem_Socket_Type":2},
                 {"Reinforce_ID":1102,"Reinforce_Type":0,"SourceItem_ID":22261102,"TargetItem_Socket_Type":2},
                 {"Reinforce_ID":1103,"Reinforce_Type":0,"SourceItem_ID":22261103,"TargetItem_Socket_Type":2},
                 {"Reinforce_ID":1200,"Reinforce_Type":0,"SourceItem_ID":22261200,"TargetItem_Socket_Type":3}]
                """);
            
            
            await File.WriteAllTextAsync(Path.Combine(dir, "RandomSet.json"), """
                [{"ID":1100,"Item":22261101,"Package":1,"Ratio":41,"NationEnable":255},
                 {"ID":1100,"Item":22261102,"Package":1,"Ratio":40,"NationEnable":255},
                 {"ID":1100,"Item":22261001,"Package":1,"Ratio":5,"NationEnable":255},
                 {"ID":1100,"Item":22261103,"Package":1,"Ratio":0,"NationEnable":255}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemSocketGroup.json"), """
                [{"SocketGroupID":1,"Socket_Group_Type1":1},
                 {"SocketGroupID":11,"Socket_Group_Type1":1,"Socket_Group_Type2":2,"Socket_Group_Type3":3,"Socket_Group_Type4":4}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "SocketGroupType.json"), """
                [{"SocketGroupTypeID":1,"TargetItem_Socket_Type1":1,"TargetItem_Socket_Type2":7},
                 {"SocketGroupTypeID":2,"TargetItem_Socket_Type1":2}]
                """);
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static InventoryItem Rune(uint itemId, int count = 3) =>
        new() { Id = 1010000008442911614, CharacterId = Owner, ItemId = itemId, Count = count, StorageType = 0, Slot = 52 };

 /// <summary>The sword with one opened, empty rune hole (CardsJson holds one entry per punched hole).</summary>
    private static InventoryItem Sword(byte storage = 0, string? holes = "[0]") =>
        new() { Id = 1010000008442911772, CharacterId = Owner, ItemId = 11100001, Count = 1, StorageType = storage, Slot = 51, CardsJson = holes };

    [Fact]
    public async Task ARuneIntoAMatchingOpenHole_IsAccepted()
    {
        var store = await Store.Value;
        Assert.Equal(AttachSocketHandler.Attached, AttachSocketHandler.Check(store, Owner, Rune(22261001), Sword(), 0, out _));
        Assert.Equal(AttachSocketHandler.Attached, AttachSocketHandler.Check(store, Owner, Rune(22261001), Sword(storage: 1), 0, out _));
    }

    [Fact]
    public async Task ACard_IsNotARune_Code3()
    {
        
        var store = await Store.Value;
        Assert.Equal((ushort)3, AttachSocketHandler.Check(store, Owner, Rune(22310892, 1), Sword(), 0, out _));
    }

    [Fact]
    public async Task AGoneOrForeignRuneStack_CannotBeFound_Code1()
    {
        
        var store = await Store.Value;
        Assert.Equal((ushort)1, AttachSocketHandler.Check(store, Owner, null, Sword(), 0, out _));
        Assert.Equal((ushort)1, AttachSocketHandler.Check(store, Owner, Rune(22261001, count: 0), Sword(), 0, out _));
        Assert.Equal((ushort)1, AttachSocketHandler.Check(store, Owner + 1, Rune(22261001), Sword(), 0, out _));
    }

    [Fact]
    public async Task AMissingTarget_Code2_AClosedHole_Code4_AWrongKind_Code5()
    {
        var store = await Store.Value;
        Assert.Equal((ushort)2, AttachSocketHandler.Check(store, Owner, Rune(22261001), null, 0, out _));
        Assert.Equal((ushort)2, AttachSocketHandler.Check(store, Owner, Rune(22261001), Sword(storage: 2), 0, out _));
        Assert.Equal((ushort)4, AttachSocketHandler.Check(store, Owner, Rune(22261001), Sword(holes: null), 0, out _));
        Assert.Equal((ushort)4, AttachSocketHandler.Check(store, Owner, Rune(22261001), Sword(), 1, out _));
        Assert.Equal((ushort)5, AttachSocketHandler.Check(store, Owner, Rune(22262001), Sword(), 0, out _));
    }

 /// <summary>The sword 11100029 with its first two holes opened and empty: hole 0 is kind 1, hole 1 kind 2.</summary>
    private static InventoryItem SeedSword() =>
        new() { Id = 1010000008442911773, CharacterId = Owner, ItemId = 11100029, Count = 1, StorageType = 1, Slot = 6, CardsJson = "[0,0]" };

    [Fact]
    public async Task ABlessing_OnATargetOfItsParts_IsAccepted_WhateverTheHoles()
    {
        
        
        var store = await Store.Value;
        Assert.Equal(AttachSocketHandler.Attached, AttachSocketHandler.Check(store, Owner, Rune(22290033), Sword(holes: null), 0, out _));
        Assert.Equal(AttachSocketHandler.Attached, AttachSocketHandler.Check(store, Owner, Rune(22290033), Sword(storage: 1), 0, out _));
    }

    [Fact]
    public async Task ABlessing_OnTheWrongPartOrLevel_IsCode6_AndWithNoTarget_Code2()
    {
        
        
        var store = await Store.Value;
        var chefTop = new InventoryItem { Id = 1010000008442911900, CharacterId = Owner, ItemId = 12611000, Count = 1, StorageType = 1, Slot = 2 };
        Assert.Equal((ushort)6, AttachSocketHandler.Check(store, Owner, Rune(22290036), Sword(), 0, out _));
        Assert.Equal((ushort)6, AttachSocketHandler.Check(store, Owner, Rune(22290036), chefTop, 0, out _));
        Assert.Equal((ushort)2, AttachSocketHandler.Check(store, Owner, Rune(22290033), null, 0, out _));
        Assert.Equal((ushort)2, AttachSocketHandler.Check(store, Owner, Rune(22290033), Sword(storage: 2), 0, out _));
    }

    [Fact]
    public async Task ASeedRune_IsCheckedAgainstTheHoleLikeAnyRune()
    {
        var store = await Store.Value;
        Assert.Equal(AttachSocketHandler.Attached, AttachSocketHandler.Check(store, Owner, Rune(22261100), SeedSword(), 1, out _));
        Assert.Equal((ushort)5, AttachSocketHandler.Check(store, Owner, Rune(22261100), SeedSword(), 0, out _));
    }

    [Fact]
    public async Task ASeedRuneWithNothingToRoll_IsRefusedWithCode6()
    {
        
        var store = await Store.Value;
        Assert.Empty(AttachSocketHandler.SeedRuneMembers(store, 22261200));
        Assert.Equal((ushort)6, AttachSocketHandler.Check(store, Owner, Rune(22261200), SeedSword(), 1, out _));
    }

    [Fact]
    public async Task SeedRuneMembers_AreItsRandomSetRunesOfItsSocketType_WithTheirRatio()
    {
        var store = await Store.Value;
        Assert.Equal(new List<(uint, uint)> { (22261101, 41), (22261102, 40) },
            AttachSocketHandler.SeedRuneMembers(store, 22261100));
        Assert.Empty(AttachSocketHandler.SeedRuneMembers(store, 22261001)); 
    }

    [Theory]
    [InlineData(0L, 22261101u)]
    [InlineData(40L, 22261101u)]
    [InlineData(41L, 22261102u)]
    [InlineData(80L, 22261102u)]
    [InlineData(81L, 0u)]
    public void PickByRatio_GivesEachMemberABandOfItsRatioWidth(long draw, uint expected)
    {
        var members = new List<(uint ItemId, uint Ratio)> { (22261101, 41), (22261102, 40) };
        Assert.Equal(expected, AttachSocketHandler.PickByRatio(members, draw));
    }

    [Fact]
    public async Task EverySeedRuneOfTheShippedData_RollsFromItsWholeRandomSet()
    {
        
        
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "RandomSet.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/RandomSet.json");
        var dir = Path.Combine(Path.GetTempPath(), "ro2-seedrunes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "ItemInfo.json", "ItemReinforceInfo.json", "RandomSet.json" })
                File.Copy(Path.Combine(root.FullName, "data", name), Path.Combine(dir, name));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            var seeds = store.Items.Values.Where(i => i.Item_Type == 49).ToList();
            Assert.Equal(45, seeds.Count);
            foreach (var seed in seeds)
            {
                var members = AttachSocketHandler.SeedRuneMembers(store, seed.ID);
                var set = store.RandomSet.Where(r => r.ID == seed.RandomSet_ID).ToList();
                Assert.NotEmpty(members);
                Assert.Equal(set.Count, members.Count);
                Assert.Equal(set.Sum(r => (long)r.Ratio), members.Sum(m => (long)m.Ratio));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    
    [InlineData((ushort)3)]
    
    [InlineData((ushort)1)]
    public void Refusal_IsTheCodeOverAZeroedRecord_AsRetailSendsIt(ushort code)
    {
        var retail = Convert.FromHexString(code.ToString("X2") + "00" + new string('0', 252));
        Assert.Equal(retail, AttachSocketHandler.BuildRefusal2786(code));
    }
}
