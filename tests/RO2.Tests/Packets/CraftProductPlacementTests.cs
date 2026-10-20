using System.Collections.Generic;
using System.Linq;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Gates for where a craft tick puts its product (<see cref="CraftItemHandler.PlanProduct"/>): into a held stack
/// it fits into, or a free bag cell (<see cref="BagCapacity.FreeCells"/>), never past them.
/// </summary>
/// <remarks>
/// The item ids are real ItemInfo rows. The bags are constructed for each case.
/// </remarks>
public class CraftProductPlacementTests
{
    private const uint Material = 33000521;
    private const uint Unstackable = 16702290;
    private const uint Stackable = 31000005;
    private const uint Filler = 1;
    private const uint Bag30 = 60000366;

    private static readonly IReadOnlyDictionary<uint, ItemData> NoBags = new Dictionary<uint, ItemData>();

    private static readonly (uint ItemId, int Count)[] ThreeMaterials = { (Material, 3) };

    private static InventoryItem Row(short slot, uint itemId = Filler, int count = 1, byte storageType = 0) =>
        new() { Id = (ulong)(1000 + slot + 100 * storageType), ItemId = itemId, Count = count, Slot = slot, StorageType = storageType };

 /// <summary>All 20 main-bag cells taken; cell 0 holds 10 of the material, the rest filler.</summary>
    private static List<InventoryItem> FullMainBag()
    {
        var bag = Enumerable.Range(1, BagCapacity.MainBagCells - 1).Select(s => Row((short)s)).ToList();
        bag.Insert(0, Row(0, Material, 10));
        return bag;
    }

    [Fact]
    public void FullBag_WithNoCellFreed_IsRefused() =>
        
        
        Assert.Null(CraftItemHandler.PlanProduct(FullMainBag(), ThreeMaterials, Unstackable, 1, 1, NoBags));

    [Fact]
    public void MaterialStackTakenToZero_FreesItsCell()
    {
        
        var bag = Enumerable.Range(0, BagCapacity.MainBagCells).Select(s => Row((short)s)).ToList();
        bag[2] = Row(2, Material, 2);
        bag[9] = Row(9, Material, 5);

        var plan = CraftItemHandler.PlanProduct(bag, ThreeMaterials, Unstackable, 1, 1, NoBags);

        Assert.NotNull(plan);
        Assert.Null(plan!.MergeInto);
        Assert.Equal(new short[] { 2 }, plan!.NewCells.ToArray());
    }

    [Fact]
    public void StackableProduct_TopsUpAHeldStack_EvenInAFullBag()
    {
        var bag = FullMainBag();
        var held = Row(4, Stackable, 10);
        bag[4] = held;

        var plan = CraftItemHandler.PlanProduct(bag, ThreeMaterials, Stackable, 1, 255, NoBags);

        Assert.NotNull(plan);
        Assert.Same(held, plan!.MergeInto);
        Assert.Empty(plan!.NewCells);
    }

    [Fact]
    public void NewCell_IsInAnEquippedBag_NotInTheCellsOfAMissingOne()
    {
        
        
        var bag = FullMainBag();
        bag.Add(Row(1, Bag30, storageType: 3));
        var items = new Dictionary<uint, ItemData> { [Bag30] = new ItemData { ID = Bag30, Bag_Size = 30 } };

        var plan = CraftItemHandler.PlanProduct(bag, ThreeMaterials, Unstackable, 1, 1, items);

        Assert.NotNull(plan);
        Assert.Equal(new short[] { BagCapacity.MainBagCells + BagCapacity.ExtraBagStride }, plan!.NewCells.ToArray());
    }
}
