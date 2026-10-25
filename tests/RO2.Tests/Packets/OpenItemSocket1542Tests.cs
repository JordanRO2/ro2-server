using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Chat;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact tests for 1542 ReqOpenItemSocket and its answer 2785 AnsOpenItemSocket (<see cref="OpenItemSocketHandler"/>).
/// The refusal code selects a message from the UI string table. Item rows are copied from the shipped item data.
/// </summary>
public class OpenItemSocket1542Tests
{
    private const uint Owner = 7;

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-opensocket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            
            
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":13231155,"Item_Type":3,"Require_Level":0,"High_Category":4,"Socket_GroupID":206,"Durability":0,"Bind_Type":0},
                 {"ID":12711604,"Item_Type":3,"Require_Level":0,"High_Category":4,"Socket_GroupID":1,"Durability":0,"Bind_Type":0},
                 {"ID":22264029,"Item_Type":29,"Require_Level":0,"High_Category":7,"Socket_GroupID":0},
                 {"ID":16702357,"Item_Type":3,"Require_Level":50,"High_Category":3,"Socket_GroupID":202,"Durability":10000,"Bind_Type":2},
                 {"ID":16702297,"Item_Type":1,"Require_Level":50,"High_Category":1,"Socket_GroupID":200,"Durability":10000,"Bind_Type":1},
                 {"ID":60000202,"Item_Type":30,"Require_Level":0,"High_Category":7},
                 {"ID":22260002,"Item_Type":30,"Require_Level":34,"High_Category":7},
                 {"ID":22260005,"Item_Type":30,"Require_Level":50,"High_Category":7},
                 {"ID":22260006,"Item_Type":30,"Require_Level":50,"High_Category":7}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemSocketGroup.json"), """
                [{"SocketGroupID":1,"Socket_Group_Type1":1},
                 {"SocketGroupID":200,"Socket_Group_Type1":6,"Socket_Group_Type2":6,"Socket_Group_Type3":7},
                 {"SocketGroupID":202,"Socket_Group_Type1":6},
                 {"SocketGroupID":206,"Socket_Group_Type1":6,"Socket_Group_Type2":6,"Socket_Group_Type3":6}]
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

 /// <summary>Abili_val of the socket punchers above, from the ItemInfo data table.</summary>
    private static int? AbiliVal(uint id) => id switch
    {
        60000202 => 0,
        22260002 => 10699,
        22260005 => 16980,
        22260006 => 0,
        _ => null,
    };

    private static InventoryItem Puncher(uint itemId, int count = 17) =>
        new() { Id = 1010000008429881942, CharacterId = Owner, ItemId = itemId, Count = count, StorageType = 0, Slot = 60 };

    private static InventoryItem Item(uint itemId, ulong uid = 1010000008436940464, byte storage = 0, short slot = 108) =>
        new() { Id = uid, CharacterId = Owner, ItemId = itemId, Count = 1, StorageType = storage, Slot = slot, Durability = 0 };

    [Fact]
    public async Task ThreeSuperPunches_OnACostumeWing_ReproduceRetailsThree2785s()
    {
        
        
        
        string[] retail =
        {
            "0000b07e060ea83d040e33e4c900000001041200000000000000000000000000000000060000ca879303" + new string('0', 172),  
            "0000b07e060ea83d040e33e4c900000001041200000000000000000000000000000000060000ca879303060000ca879303" + new string('0', 158), 
            "0000b07e060ea83d040e33e4c900000001041200000000000000000000000000000000060000ca879303060000ca879303060000ca879303" + new string('0', 144), 
        };
        var store = await Store.Value;
        var wing = Item(13231155);
        foreach (var frame in retail)
        {
            Assert.Equal(OpenItemSocketHandler.Opened,
                OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(60000202), wing, out int hole, out _));
            OpenItemSocketHandler.Punch(wing, hole, 60000202);
            Assert.Equal(Convert.FromHexString(frame),
                OpenItemSocketHandler.BuildAnswer2785(ItemWorkbench.RecordWithRuneholes(wing, store)));
        }
        
        Assert.Equal(OpenItemSocketHandler.NoMoreHoles,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(60000202), wing, out _, out _));
    }

    [Fact]
    public async Task NoPuncher_IsCode1_OverAZeroRecord()
    {
        
        var store = await Store.Value;
        Assert.Equal(OpenItemSocketHandler.NoPuncher,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, null, Item(16702297), out _, out _));
        Assert.Equal(Convert.FromHexString("0100" + new string('0', 252)), OpenItemSocketHandler.BuildRefusal2785(1));
        
        Assert.Equal(OpenItemSocketHandler.NoPuncher,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner + 1, Puncher(22260005), Item(16702297), out _, out _));
        Assert.Equal(OpenItemSocketHandler.NoPuncher,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005, count: 0), Item(16702297), out _, out _));
    }

    [Fact]
    public async Task ARuneAsTheTarget_IsCode4_NotCode6()
    {
        
        
        
        var store = await Store.Value;
        Assert.Equal(OpenItemSocketHandler.NoMoreHoles,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005), Item(22264029), out _, out _));
        Assert.Equal(Convert.FromHexString("0400" + new string('0', 252)), OpenItemSocketHandler.BuildRefusal2785(4));
    }

    [Fact]
    public async Task AGearPuncherOnAWornCostume_IsCode6()
    {
        
        
        var store = await Store.Value;
        var costume = Item(12711604, storage: 1, slot: 16);
        Assert.Equal(OpenItemSocketHandler.WrongPart,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260002), costume, out _, out _));
        Assert.Equal(Convert.FromHexString("0600" + new string('0', 252)), OpenItemSocketHandler.BuildRefusal2785(6));
        Assert.Null(costume.CardsJson);
    }

    [Fact]
    public async Task EachPuncherPart_OpensOnlyItsOwnHighCategory()
    {
        
        
        var store = await Store.Value;
        Assert.Equal(OpenItemSocketHandler.Opened, OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260006), Item(16702357), out _, out _));
        Assert.Equal(OpenItemSocketHandler.WrongPart, OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005), Item(16702357), out _, out _));
        Assert.Equal(OpenItemSocketHandler.Opened, OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005), Item(16702297), out _, out _));
        Assert.Equal(OpenItemSocketHandler.WrongPart, OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260006), Item(16702297), out _, out _));
        
        Assert.Equal(OpenItemSocketHandler.WrongLevel, OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(60000202), Item(16702297), out _, out _));
        
        Assert.Equal(OpenItemSocketHandler.Opened, OpenItemSocketHandler.Check(store, _ => null, Owner, Puncher(22260006), Item(16702297), out _, out _));
    }

    [Fact]
    public async Task ATargetAboveThePunchersLevel_IsCode5()
    {
        
        
        var store = await Store.Value;
        Assert.Equal(OpenItemSocketHandler.WrongLevel,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260002), Item(16702297), out _, out _));
    }

    [Fact]
    public async Task ATargetInTheBank_IsCode3()
    {
        var store = await Store.Value;
        Assert.Equal(OpenItemSocketHandler.NoTarget,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005), null, out _, out _));
        Assert.Equal(OpenItemSocketHandler.NoTarget,
            OpenItemSocketHandler.Check(store, AbiliVal, Owner, Puncher(22260005), Item(16702297, storage: 2), out _, out _));
    }

    [Fact]
    public void ThePuncherPart_ComesFromRequireLevelAndAbiliVal()
    {
        
        
        Assert.Equal(OpenItemSocketHandler.PuncherPart.Costume, OpenItemSocketHandler.PuncherPartOf(0, 0));
        Assert.Equal(OpenItemSocketHandler.PuncherPart.Accessory, OpenItemSocketHandler.PuncherPartOf(50, 0));
        Assert.Equal(OpenItemSocketHandler.PuncherPart.Gear, OpenItemSocketHandler.PuncherPartOf(50, 16980));
    }
}

/// <summary>
/// A friend request is answered with result 33 when the requester's friend list is full (<see cref="FriendStore"/>).
/// </summary>
public class FriendListFull23508Tests
{
    [Fact]
    public void AFullList_IsAnswered33_OverTheAllZeroRecord()
    {
        
        
        var retail = Convert.FromHexString("2100110000000000000000000000000000000000000000");
        Assert.Equal(retail, FriendStore.BuildAddFriendAnswer(FriendStore.AddResultListFull, null));
    }

    [Fact]
    public void TheLimit_IsTheLargestRetailList()
    {
        
        Assert.False(FriendStore.IsListFull(199));
        Assert.True(FriendStore.IsListFull(200));
    }
}

/// <summary>
/// Reinforce transfer (1682, <see cref="TransferReinforceItemHandler"/>, <see cref="RefineTransferRules"/>).
/// Item rows are copied from the shipped item, card enforce, refine and config data.
/// </summary>
public class RefineTransfer1682Tests
{
    private const uint Owner = 7;

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-refinetransfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":60000758,"Item_Type":118},{"ID":60000801,"Item_Type":118},{"ID":70000889,"Item_Type":118},
                 {"ID":22310796,"Item_Type":13,"Grade":4,"Durability":10000,"Bind_Type":1},
                 {"ID":22310832,"Item_Type":13,"Grade":4,"Durability":10000,"Bind_Type":1},
                 {"ID":22310852,"Item_Type":13,"Grade":4,"Durability":10000,"Bind_Type":1},
                 {"ID":22320050,"Item_Type":139,"Grade":5,"Durability":10000,"Bind_Type":1},
                 {"ID":16702100,"Item_Type":2,"Grade":4,"Durability":10000,"Bind_Type":1,"Equip_Slot":4},
                 {"ID":16702158,"Item_Type":2,"Grade":4,"Durability":10000,"Bind_Type":1,"Equip_Slot":1}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "REFINE_Trans_ITEM.json"), """
                [{"ITEM_ID":70000889,"Succ_Rate":2000,"DISC_RATE":0,"Max_Value":7500,"limit_level":5},
                 {"ITEM_ID":60000758,"Succ_Rate":20000,"DISC_RATE":10000,"Max_Value":10000,"limit_level":25},
                 {"ITEM_ID":60000801,"Succ_Rate":8000,"DISC_RATE":10000,"Max_Value":10000,"limit_level":5}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardEnforceInfo.json"), """
                [{"Item_ID":22310796,"EnforceID":450},{"Item_ID":22310832,"EnforceID":450},
                 {"Item_ID":22310852,"EnforceID":450},{"Item_ID":22320050,"EnforceID":550}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "Config_Def.json"), """
                [{"type":83,"Value":"5000"},{"type":107,"Value":"300"},{"type":108,"Value":"0.5"}]
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

 /// <summary>Item_Lv of the rows above, from the ItemInfo data table.</summary>
    private static int? ItemLv(uint id) => id switch
    {
        22310796 or 22310832 or 22310852 => 1,
        22320050 => 50,
        16702100 or 16702158 => 95,
        60000758 or 60000801 or 70000889 => 0,
        _ => null,
    };

    private static InventoryItem Bag(ulong uid, uint itemId, byte grade, int count = 1, short slot = 60) =>
        new() { Id = uid, CharacterId = Owner, ItemId = itemId, Count = count, StorageType = 0, Slot = slot, Refine = grade, Durability = 10000 };

    [Fact]
    public async Task ASuccess_ReproducesRetails2379_2329_And3014()
    {
        
        
        
        var store = await Store.Value;
        var ticket = Bag(1010000005990989803, 60000758, 0, slot: 107);
        var target = Bag(1010000008441608574, 22310796, 0, count: 9, slot: 101);
        var source = Bag(1010000008441168476, 22310796, 10, slot: 102);

        Assert.Null(TransferReinforceItemHandler.Check(store, ItemLv, Owner, ticket, target, source, out var terms));
        Assert.Equal(new RefineTransferRules.Terms(0, 10, 10), terms);
        target.Refine = RefineTransferRules.Outcome(terms);

        Assert.Equal(Convert.FromHexString("028a008a00c7e211b6ac0d000000005c02470ea83d040e8c6f5401000000040c" + new string('0', 218)),
            ShopItemPackets.BuildDestroyItem2379(0x0DACB611, TransferReinforceItemHandler.DestroyedSourceRecord(source)));
        Assert.Equal(Convert.FromHexString("01161600a6e211b6ac0d00001104eb3f3c7ca73d040e0001"),
            CombatPackets.BuildUseItem2329(0x0DACB611, 17, 4, ticket.Id, 0, useSource: 1));
        Assert.Equal(Convert.FromHexString("00007eb94d0ea83d040e8c6f5401102709040b0000000000000000000000000a" + new string('0', 192)),
            TransferReinforceItemHandler.BuildAnswer3014(0, ItemWorkbench.RecordWithRuneholes(target, store)));
    }

    [Fact]
    public void ASub10000Ticket_GivesWhatRetailGave()
    {
        
        
        var rule = new RO2.Domain.GameData.Tables.REFINETransITEM { ITEM_ID = 60000801, Succ_Rate = 8000, DISC_RATE = 10000, Max_Value = 10000, limit_level = 5 };
        var config = (5000f, 300f, 0.5f);
        var nine = RefineTransferRules.Compute(rule, 10, 9, 1, 1, config);
        var eight = RefineTransferRules.Compute(rule, 10, 8, 1, 1, config);
        Assert.Equal(new RefineTransferRules.Terms(0, 9, 10), nine);
        Assert.Equal(new RefineTransferRules.Terms(0, 8, 10), eight);
        Assert.Equal(9, RefineTransferRules.Outcome(nine));
        Assert.Equal(9, RefineTransferRules.Outcome(eight));
    }

    [Fact]
    public async Task RetailsRefusals_AreRefused()
    {
        var store = await Store.Value;
        
        Assert.NotNull(TransferReinforceItemHandler.Check(store, ItemLv, Owner,
            Bag(1, 60000758, 0), Bag(2, 22320050, 0), Bag(3, 22310796, 10), out _));
        
        Assert.NotNull(TransferReinforceItemHandler.Check(store, ItemLv, Owner,
            Bag(1, 60000801, 0), Bag(2, 22310852, 0, count: 3), Bag(3, 16702100, 20), out _));
        Assert.Equal(Convert.FromHexString("1462" + new string('0', 252)),
            TransferReinforceItemHandler.BuildAnswer3014(TransferReinforceItemHandler.TransferRefused, null));
    }

    [Fact]
    public async Task TheClientsGates_AndAFee_AreRefused()
    {
        var store = await Store.Value;
        
        Assert.NotNull(TransferReinforceItemHandler.Check(store, ItemLv, Owner,
            Bag(1, 60000758, 0), Bag(2, 22310832, 10), Bag(3, 22310796, 10), out _));
        
        Assert.NotNull(TransferReinforceItemHandler.Check(store, ItemLv, Owner,
            Bag(1, 70000889, 0), Bag(2, 22310832, 0), Bag(3, 22310796, 10), out var terms));
        Assert.Equal(5000, terms.Fee);
    }

    [Fact]
    public async Task ArmourOntoArmour_Passes()
    {
        
        
        var store = await Store.Value;
        Assert.Null(TransferReinforceItemHandler.Check(store, ItemLv, Owner,
            Bag(1, 60000758, 0), Bag(2, 16702158, 0), Bag(3, 16702100, 10), out var terms));
        Assert.Equal(new RefineTransferRules.Terms(0, 10, 10), terms);
    }

    [Fact]
    public void ItemLevelsAndAbiliVal_LoadFromTheShippedItemInfo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "ItemInfo.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "data", "ItemInfo.json");
        var levels = RefineTransferRules.LoadItemLevels(path);
        Assert.NotNull(levels);
        Assert.Equal(1, levels[22310796]);
        Assert.Equal(50, levels[22320050]);
        Assert.Equal(95, levels[16702100]);
        var abili = OpenItemSocketHandler.LoadAbiliVal(path);
        Assert.NotNull(abili);
        Assert.Equal(16980, abili[22260005]);
        Assert.Equal(0, abili[22260006]);
    }
}
