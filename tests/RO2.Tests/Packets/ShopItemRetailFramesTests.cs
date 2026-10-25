using System;
using System.Collections.Generic;
using System.Linq;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Economy.Domain;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>Byte-exact tests for shop and item-operation answers (2375, 2376, 2417, 3009) and for the placement rules behind 1328 and 1334.</summary>
public class ShopItemRetailFramesTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    
    [Fact]
    public void BuyRefusal2375_ReproducesRetailPossessionRefusal()
    {
        
        var expected = Hex("0129" + "2900c1e211fd100ac64e000000000000000000000000000000000000000000000055ad530100000000");
        Assert.Equal(expected, ShopItemPackets.BuildBuyRefusal2375(0x0A10FD11u, 20166, 0, 22261077u));
    }

    [Fact]
    public void BuyRefusal2375_ReproducesRetailGenericRefusalWithTokenRow()
    {
        
        var expected = Hex("0129" + "2900c1e211df460a01000000000000000000000000000000000000000000f4070099c4c90100000000");
        Assert.Equal(expected, ShopItemPackets.BuildBuyRefusal2375(0x0A46DF11u, 1, 2036, 30000281u));
    }

    [Fact]
    public void BuyRefusal2375_ReproducesRetailBagFullRefusal()
    {
        
        var expected = Hex("0129" + "2900c1e211444800344e0000000000000000000000000000000000000000410800ef87930300000000");
        Assert.Equal(expected, ShopItemPackets.BuildBuyRefusal2375(0x00484411u, 20020, 2113, 60000239u));
    }

    
    [Fact]
    public void SellItem2376_ReproducesRetailNothingSoldAnswer()
    {
        
        
        var expected = Hex("01636300c3e21158f306010036668a01000000002768820da83d040e4468820da83d040e3468820da83d040e"
            + new string('0', 2 * (99 - 42)));
        var body = ShopItemPackets.BuildSellItem2376(0x06F35811u, ShopItemPackets.SellResultNothingSold, 25847350L,
            new ulong[] { 0x0E043DA80D826827UL, 0x0E043DA80D826844UL, 0x0E043DA80D826834UL }, Array.Empty<byte[]>());
        Assert.Equal(expected, body);
    }

    [Fact]
    public void SellItem2376_ReproducesRetailSale()
    {
        
        
        
        const string record = "7faf9912a83d040e2f9cdf00102700040a";
        var rec = Hex(record + new string('0', 2 * (126 - record.Length / 2)));
        var expected = Hex("02e100" + "e100c3e211aa690b0000527c4cd3010000007faf9912a83d040e"
            + new string('0', 2 * 72) + "01" + Convert.ToHexString(rec).ToLowerInvariant());
        var body = ShopItemPackets.BuildSellItem2376(0x0B69AA11u, 0, 7839972434L,
            new ulong[] { 0x0E043DA81299AF7FUL }, new[] { rec });
        Assert.Equal(expected, body);
    }

    
    [Fact]
    public void RandomRefine3009_ReproducesRetailNoMaterialAnswer()
    {
        
        
        var expected = Hex("028800" + "880098e4110c6e0b0300b1d56e10a83d040e00000000000001041b"
            + new string('0', 2 * (136 - 27)));
        var record = RandomRefineItemHandler.DescriptorEcho(0x0E043DA8106ED5B1UL, 1, 4, 0x1B);
        var body = ShopItemPackets.BuildRandomRefineItem3009(0x0B6E0C11u, RandomRefineItemHandler.ResultNoMaterial, record);
        Assert.Equal(expected, body);
    }

    
    [Fact]
    public void BreakDown2417_ReproducesRetailPotionAnswerWithNoOutput()
    {
        
        
        
        var expected = Hex("01171700fae2112bee07000000000103894bc00ca83d040eff");
        var body = ShopItemPackets.BuildBreakDownItem2417(0x07EE2B11u, 0, 1, 3, 0x0E043DA80CC04B89UL, 255,
            Array.Empty<byte[]>());
        Assert.Equal(expected, body);
    }

    [Fact]
    public void BreakDown2417_ReproducesRetailPotionAnswerWithOneOutput()
    {
        
        
        const string record = "aabcf70ca83d040ee1d35501000004013500";
        var rec = Hex(record + new string('0', 2 * (126 - record.Length / 2)));
        var expected = Hex("0295009500fae2112bee07000001010103894bc00ca83d040efc" + Convert.ToHexString(rec).ToLowerInvariant());
        var body = ShopItemPackets.BuildBreakDownItem2417(0x07EE2B11u, 0, 1, 3, 0x0E043DA80CC04B89UL, 252, new[] { rec });
        Assert.Equal(expected, body);
    }

    [Fact]
    public void ResolveBreakResult_TakesTheGroupRowWhenIdOneIsUnset()
    {
        var group = new BreakResult { Result_ID = 23, BreakResultItemID1 = 22402020, BreakResultItemNum1 = 1, BreakResultRate1 = 33,
            BreakResultItemID2 = 22402017, BreakResultItemNum2 = 1, BreakResultRate2 = 166 };
        var direct = new BreakResult { Result_ID = 850001, BreakResultItemID1 = 33000713, BreakResultItemNum1 = 1, BreakResultRate1 = 2500 };
        var table = new[] { direct, group };

        var potion = new ItemBreakInfo { Item_ID = 21117020, BreakNeedItemID = 22402031, BreakNeedItemNum = 10,
            BreakResultID_1 = 0, BreakResultID_2 = 23 };
        var weapon = new ItemBreakInfo { Item_ID = 11152511, BreakNeedItemID = 22210003, BreakNeedItemNum = 1,
            BreakResultID_1 = 850001, BreakResultID_2 = 0 };

        Assert.Same(group, BreakDownItemHandler.ResolveBreakResult(potion, table));
        Assert.Same(direct, BreakDownItemHandler.ResolveBreakResult(weapon, table));
        Assert.Null(BreakDownItemHandler.ResolveBreakResult(new ItemBreakInfo { Item_ID = 1, BreakResultID_2 = 4 }, table));
    }

    private static InventoryItem Stack(ulong id, uint item, int count, short slot, byte storage = 0)
        => new() { Id = id, CharacterId = 7, ItemId = item, Count = count, Slot = slot, StorageType = storage };

    [Fact]
    public void PlanStoneTake_EmptiesTheLowestCellFirstAndTakesTheRestFromTheNext()
    {
        
        
        const uint Stone = 22402031;
        var named = Stack(100, Stone, 255, 49);
        var low = Stack(101, Stone, 5, 5);
        var next = Stack(102, Stone, 255, 6);
        var bag = new[] { named, Stack(103, 21117020, 200, 1), next, low, Stack(104, Stone, 255, 0, storage: 2) };

        var takes = BreakDownItemHandler.PlanStoneTake(bag, Stone, 10, sourceUid: 103);

        Assert.NotNull(takes);
        Assert.Equal(new[] { (101UL, 0), (102UL, 250) }, takes!.Select(t => (t.Stack.Id, t.Left)).ToArray());
    }

    [Fact]
    public void PlanStoneTake_RefusesWhenTheBagHoldsTooFew()
    {
        const uint Stone = 22402031;
        var bag = new[] { Stack(1, Stone, 4, 2), Stack(2, Stone, 5, 3), Stack(3, Stone, 50, 4, storage: 2) };
        Assert.Null(BreakDownItemHandler.PlanStoneTake(bag, Stone, 10, sourceUid: 99));
        Assert.Equal(2, BreakDownItemHandler.PlanStoneTake(bag, Stone, 9, sourceUid: 99)!.Count);
    }

    
    private static readonly IReadOnlyDictionary<uint, ItemData> NoBags = new Dictionary<uint, ItemData>();

    /// <summary>The placement of a buy on <paramref name="bag"/>: the host's bag read (free cells from BagCapacity) and the economy planner.</summary>
    private static ShopBuyPlan? PlanBuy(IReadOnlyList<InventoryItem> bag, uint itemId, int quantity, byte stackMax)
    {
        var view = ShopInventoryAdapter.BagOf(bag, NoBags);
        return ShopBuyPlanner.Plan(view.Stacks, view.FreeCells, itemId, quantity, stackMax);
    }

    [Fact]
    public void PlanBuy_RestacksOntoTheLowestCellStackTheQuantityFits()
    {
        var bag = new List<InventoryItem> { Stack(1, 34000473, 95, 7), Stack(2, 34000473, 30, 9), Stack(3, 34000473, 20, 3) };
        var plan = PlanBuy(bag, 34000473, 5, stackMax: 99);
        Assert.NotNull(plan);
        Assert.Equal(3UL, plan!.RestackId);   
        Assert.Empty(plan.NewStacks);
    }

    [Fact]
    public void PlanBuy_SplitsIntoNewStacksInFreeCellsAndRefusesAFullBag()
    {
        
        var bag = Enumerable.Range(0, 17).Select(i => Stack((ulong)(10 + i), 70000001, 1, (short)(i < 5 ? i : i + 1))).ToList();
        var plan = PlanBuy(bag, 34000473, 255, stackMax: 99);
        Assert.NotNull(plan);
        Assert.Null(plan!.RestackId);
        Assert.Equal(new (short, int)[] { (5, 99), (18, 99), (19, 57) }, plan.NewStacks.ToArray());

        bag.Add(Stack(99, 70000001, 1, 5));
        Assert.Null(PlanBuy(bag, 34000473, 255, stackMax: 99));   
        Assert.NotNull(PlanBuy(bag, 34000473, 198, stackMax: 99)); 
    }
}
