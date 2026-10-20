using System;
using System.Collections.Generic;
using System.Linq;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The worn craft-quantity bonus (<see cref="CraftTraitBonus"/>): TraitInfo trait 106 on the two craft labels,
/// applied to Cooking and Alchemy ticks only.
/// </summary>
public class CraftTraitBonusTests
{
    private const uint GoldLabel = 60000751;
    private const uint SilverLabel = 60000752;

    private static readonly Lazy<IReadOnlyList<TraitInfo>> Traits =
        new(() => BankBagSizeChange2345Tests.Table<TraitInfo>("TraitInfo.json"));

    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes =
        new(() => BankBagSizeChange2345Tests.Table<CraftItemList>("Craft_ItemList.json"));

    private static CraftItemList Recipe(ushort id) => Recipes.Value.First(r => r.ID == id);

    private static InventoryItem Item(uint itemId, byte storageType, short slot = 0) =>
        new() { Id = 7000u + (ulong)slot, ItemId = itemId, Count = 1, Slot = slot, StorageType = storageType };

    [Fact]
    public void OnlyTheTwoLabels_CarryTrait106()
    {
        var carriers = Traits.Value.Where(t => t.Trait_Type == CraftTraitBonus.QuantityTrait)
            .Select(t => (t.ItemID, t.Trait_Kind, t.Value)).OrderBy(t => t.ItemID).ToList();
        Assert.Equal(new[] { (GoldLabel, (byte)1, 10000f), (SilverLabel, (byte)1, 10000f) }, carriers);
    }

    [Theory]
    [InlineData(GoldLabel)]
    [InlineData(SilverLabel)]
    public void WornLabel_Gives10000(uint label) =>
        Assert.Equal(10000, CraftTraitBonus.WornQuantityBonus(Traits.Value,
            new[] { Item(33000521, 0), Item(label, 1, 18) }));

    [Fact]
    public void LabelInTheBag_GivesNothing() =>
        Assert.Equal(0, CraftTraitBonus.WornQuantityBonus(Traits.Value, new[] { Item(GoldLabel, 0, 3) }));

    [Fact]
    public void LabelInTheBank_GivesNothing() =>
        Assert.Equal(0, CraftTraitBonus.WornQuantityBonus(Traits.Value, new[] { Item(GoldLabel, 2, 3) }));

    [Theory]
    
    [InlineData((ushort)346, 10000, 2)]
    [InlineData((ushort)346, 0, 1)]
    
    [InlineData((ushort)436, 10000, 10)]
    [InlineData((ushort)436, 0, 5)]
    
    [InlineData((ushort)1620, 10000, 1)]
    [InlineData((ushort)1598, 10000, 1)]
    public void ProductPerTick_MultipliesCookingAndAlchemyOnly(ushort recipeId, int bonus, int expected) =>
        Assert.Equal(expected, CraftTraitBonus.ProductPerTick(Recipe(recipeId), bonus));

    [Theory]
    
    
    
    [InlineData((ushort)381, true, 0x3D62,
        "028a008a0019e4110ba1020000623dd524d50ea83d040ec105d901000002021b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c80400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "028a008a0019e4110ba1020000623dd524d50ea83d040ec105d901000004021b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c80400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    
    
    [InlineData((ushort)348, false, 0x0258,
        "028a008a0019e411a12e03000058027f75da0ea83d040e0113dc0100004c013900000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000970000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "028a008a0019e411a12e03000058027f75da0ea83d040e0113dc0100004d013900000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000970000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    
    
    [InlineData((ushort)365, false, 0x03DE,
        "028a008a0019e411f210040000de03dcdef90ea83d040e4a384201000005040400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ad0e00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "028a008a0019e411f210040000de03dcdef90ea83d040e4a38420100000a040400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ad0e00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void RetailTick_GrowsTheStackByTheTicksQuantity(ushort recipeId, bool wearsGoldLabel, int sp,
        string previousHex, string nextHex)
    {
        var previous = Convert.FromHexString(previousHex);
        var next = Convert.FromHexString(nextHex);
        uint crafter = BitConverter.ToUInt32(next, 7);
        var recipe = Recipe(recipeId);

        
        Assert.Equal(BitConverter.ToUInt64(previous, 15), BitConverter.ToUInt64(next, 15));
        Assert.Equal(recipe.Craft_Item_ID, BitConverter.ToUInt32(next, 23));
        var worn = wearsGoldLabel ? new[] { Item(GoldLabel, 1, 18) } : Array.Empty<InventoryItem>();
        int perTick = CraftTraitBonus.ProductPerTick(recipe, CraftTraitBonus.WornQuantityBonus(Traits.Value, worn));
        Assert.Equal(previous[15 + 14] + perTick, next[15 + 14]);

        
        Assert.Equal(next, ShopItemPackets.BuildAckMakeCraftItemInfo2541(crafter, (ushort)sp, next.AsSpan(15).ToArray()));
    }

    [Fact]
    public void ProductPerTick_WithNoBonus_IsTheOneArgumentCount()
    {
        
        
        foreach (var recipe in Recipes.Value)
            Assert.Equal(CraftItemHandler.ProductPerTick(recipe), CraftTraitBonus.ProductPerTick(recipe, 0));
    }
}
