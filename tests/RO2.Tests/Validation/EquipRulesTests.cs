using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

public class EquipRulesTests
{
    private static readonly EquipActor Hero = new(Level: 60, Job: 7, Sex: 1);
    private static readonly InventoryItem[] Empty = Array.Empty<InventoryItem>();

    private static InventoryItem Row(ulong id, byte storage, short slot) => new() { Id = id, ItemId = 1, StorageType = storage, Slot = slot };

    
    private static ItemData Sword() => new() { ID = 1, Item_Type = 1, Equip_Slot = 6, Equip_Type = 2, Require_Level = 50, Require_Job = 64, Require_Sex = 3 };
    private static ItemData Armour(ushort slot = 2) => new() { ID = 2, Item_Type = 2, Equip_Slot = slot, Equip_Type = (ushort)(18 + slot) };
    private static ItemData Ring() => new() { ID = 3, Item_Type = 3, Equip_Slot = 10, Equip_Type = 26 };
    private static ItemData Earring() => new() { ID = 4, Item_Type = 3, Equip_Slot = 9, Equip_Type = 25 };
    private static ItemData Card() => new() { ID = 5, Item_Type = 13, Equip_Slot = 0, Equip_Type = 35, Stack_Max = 20 };
    private static ItemData Bag() => new() { ID = 6, Item_Type = 7, Bag_Size = 10 };
    private static ItemData Costume(ushort slot, byte overlap) => new() { ID = 7, Item_Type = 3, Equip_Slot = slot, Equip_Type = 30, Equip_Slot_Overlap = overlap };

    private static EquipRefusal Place(ItemData t, byte storage, int slot, EquipActor? who = null, params InventoryItem[] rows)
        => EquipRules.CanPlace(new InventoryItem { Id = 100, ItemId = t.ID }, t, storage, slot, who ?? Hero, rows);

    [Fact]
    public void LegalEquipPasses()
    {
        Assert.Equal(EquipRefusal.None, Place(Sword(), 1, 6));
        Assert.Equal(EquipRefusal.None, Place(Armour(2), 1, 2));
    }

    [Theory]
    [InlineData(7)]    
    [InlineData(0)]    
    [InlineData(30)]   
    public void WrongSlotIsRejected(int slot)
        => Assert.Equal(EquipRefusal.WrongSlot, Place(Sword(), 1, slot));

    [Fact]
    public void ArmourOnAWeaponSlotIsRejected()
        => Assert.Equal(EquipRefusal.WrongSlot, Place(Armour(2), 1, 6));

    [Fact]
    public void UnderLevelIsRejected()
        => Assert.Equal(EquipRefusal.LevelTooLow, Place(Sword(), 1, 6, new EquipActor(49, 7, 1)));

    [Fact]
    public void WrongJobIsRejectedAndNoRequirementMeansAnyJob()
    {
        Assert.Equal(EquipRefusal.JobNotAllowed, Place(Sword(), 1, 6, new EquipActor(60, 6, 1)));   
        Assert.Equal(EquipRefusal.JobNotAllowed, Place(Sword(), 1, 6, new EquipActor(60, 0, 1)));   
        Assert.Equal(EquipRefusal.None, Place(Armour(), 1, 2, new EquipActor(60, 0, 1)));           
    }

    [Fact]
    public void WrongSexIsRejected()
    {
        var female = Armour() with { Require_Sex = 2 };
        Assert.Equal(EquipRefusal.SexNotAllowed, Place(female, 1, 2, new EquipActor(60, 7, 1)));
        Assert.Equal(EquipRefusal.None, Place(female, 1, 2, new EquipActor(60, 7, 2)));
        Assert.Equal(EquipRefusal.None, Place(Armour() with { Require_Sex = 3 }, 1, 2, new EquipActor(60, 7, 1)));
    }

    [Fact]
    public void OccupiedCellIsRejectedUnlessItIsTheSwapPartnerOrTheItemsOwnRow()
    {
        var other = Row(200, 1, 6);
        Assert.Equal(EquipRefusal.DestinationOccupied, Place(Sword(), 1, 6, null, other));
        Assert.Equal(EquipRefusal.None, EquipRules.CanPlace(new InventoryItem { Id = 100 }, Sword(), 1, 6, Hero, new[] { other }, vacatingUid: 200));
        Assert.Equal(EquipRefusal.None, EquipRules.CanPlace(new InventoryItem { Id = 200 }, Sword(), 1, 6, Hero, new[] { other }));
        
        Assert.Equal(EquipRefusal.None, Place(Sword(), 1, 6, null, Row(201, 0, 6)));
    }

    [Fact]
    public void ItemWithNoWornSlotIsNotEquippable()
    {
        var potion = new ItemData { ID = 9, Item_Type = 4, Equip_Slot = 0 };
        Assert.Equal(EquipRefusal.NotEquippable, Place(potion, 1, 6));
        Assert.Equal(EquipRefusal.NotEquippable, Place(new ItemData { ID = 10, Item_Type = 3, Equip_Slot = 101, Equip_Type = 33 }, 1, 6));
    }

    [Fact]
    public void ItemTypeAndEquipTypeMustMatchTheSlotClass()
    {
        Assert.Equal(EquipRefusal.WrongItemType, Place(Armour() with { Item_Type = 4 }, 1, 2));
        Assert.Equal(EquipRefusal.WrongEquipType, Place(Armour() with { Equip_Type = 22 }, 1, 2));
        Assert.Equal(EquipRefusal.WrongEquipType, Place(Sword() with { Equip_Type = 11 }, 1, 6));   
    }

    [Fact]
    public void AccessoryWornSlotIsNotAlwaysEquipSlot()
    {
        
        var ring = Ring();
        Assert.Equal(new[] { 10, 11 }, EquipRules.WornSlotsFor(ring));
        Assert.Equal(EquipRefusal.None, Place(ring, 1, 10));
        Assert.Equal(EquipRefusal.None, Place(ring, 1, 11));
        Assert.Equal(EquipRefusal.WrongSlot, Place(ring, 1, 12));
        Assert.Equal(EquipRefusal.WrongSlot, Place(ring, 1, 9));
        
        Assert.Equal(new[] { 9 }, EquipRules.WornSlotsFor(Earring()));
        Assert.Equal(EquipRefusal.WrongSlot, Place(Earring(), 1, 11));
    }

    [Fact]
    public void CardsWearInTheCardCellsOnly()
    {
        for (int s = 20; s <= 24; s++) Assert.Equal(EquipRefusal.None, Place(Card(), 1, s));
        Assert.Equal(EquipRefusal.WrongSlot, Place(Card(), 1, 25));
        Assert.Equal(EquipRefusal.WrongSlot, Place(Card(), 1, 6));
        Assert.Equal(EquipRefusal.WrongSlot, Place(Card(), 1, 1));
    }

    [Fact]
    public void StorageTypeLegalityAndCellRanges()
    {
        Assert.Equal(EquipRefusal.IllegalStorageType, Place(Sword(), 4, 0));
        Assert.Equal(EquipRefusal.SlotOutOfRange, Place(Sword(), 1, 50));
        Assert.Equal(EquipRefusal.SlotOutOfRange, Place(Sword(), 1, -1));
        Assert.Equal(EquipRefusal.SlotOutOfRange, Place(Sword(), 0, 125));
        Assert.Equal(EquipRefusal.None, Place(Sword(), 0, 124));
        Assert.Equal(EquipRefusal.SlotOutOfRange, Place(Sword(), 2, 120));
        Assert.Equal(EquipRefusal.None, Place(Sword(), 2, 119));
        
        Assert.Equal(EquipRefusal.None, Place(Sword(), 0, 3, new EquipActor(1, 1, 1)));
        
        Assert.Equal(EquipRefusal.DestinationOccupied, Place(Sword(), 0, 3, null, Row(300, 0, 3)));
    }

    [Fact]
    public void BagWindowTakesOnlyBagsInCellsZeroToTwo()
    {
        Assert.Equal(EquipRefusal.None, Place(Bag(), 3, 0));
        Assert.Equal(EquipRefusal.None, Place(Bag(), 3, 2));
        Assert.Equal(EquipRefusal.SlotOutOfRange, Place(Bag(), 3, 3));
        Assert.Equal(EquipRefusal.NotEquippable, Place(Bag() with { Item_Type = 39 }, 3, 0));   
        Assert.Equal(EquipRefusal.NotEquippable, Place(Sword(), 3, 0));
        Assert.Equal(EquipRefusal.DestinationOccupied, Place(Bag(), 3, 1, null, Row(400, 3, 1)));
    }

    [Fact]
    public void UnknownTemplateIsRejected()
        => Assert.Equal(EquipRefusal.UnknownTemplate, EquipRules.CanPlace(new InventoryItem(), null, 1, 6, Hero, Empty));

 /// <summary>The overlap check is opt-in (enforceOverlap: true). The live wiring leaves it off because the meaning of
 /// Equip_Slot_Overlap is unverified; this test enables it to pin the logic.</summary>
    private static EquipRefusal Overlap(ItemData t, int slot, params InventoryItem[] rows)
        => EquipRules.CanPlace(new InventoryItem { Id = 100, ItemId = t.ID }, t, 1, slot, Hero, rows, vacatingUid: 0, enforceOverlap: true);

    [Fact]
    public void CostumeOverlapRefusesWhileACoveredSlotIsWorn_OnlyWhenEnforcementIsOptedIn()
    {
        
        var body = Costume(16, 216);
        Assert.Equal(EquipRefusal.None, Overlap(body, 16));
        Assert.Equal(EquipRefusal.OverlapConflict, Overlap(body, 16, Row(500, 1, 17)));
        Assert.Equal(EquipRefusal.OverlapConflict, Overlap(body, 16, Row(501, 1, 26)));
        Assert.Equal(EquipRefusal.None, Overlap(body, 16, Row(502, 1, 18)));   
        
        
        Assert.Equal(EquipRefusal.None, Place(body, 1, 16, null, Row(500, 1, 17)));
        Assert.Equal(EquipRefusal.None, Place(body, 1, 16, null, Row(501, 1, 26)));
    }

    
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static List<ItemData> LoadItemInfo()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "ItemInfo.json")))
            root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("no ancestor holds data/ItemInfo.json");
        using var stream = File.OpenRead(Path.Combine(root.FullName, "data", "ItemInfo.json"));
        return JsonSerializer.Deserialize<List<ItemData>>(stream, JsonOpts)!;
    }

    [Fact]
    public void EveryRealWearableRowEquipsInItsOwnSlotsForAQualifiedPlayer()
    {
        
        
        var misses = new List<string>();
        int checkedRows = 0;
        foreach (var t in LoadItemInfo())
        {
            bool customisation = t.Equip_Slot >= 101 && t.Equip_Slot <= 103;
            if (customisation || (t.Equip_Slot == 0 && t.Item_Type is not (13 or 139))) continue;
            var worn = EquipRules.WornSlotsFor(t);
            if (worn.Count == 0) { misses.Add($"{t.ID}: no worn slots for Equip_Slot {t.Equip_Slot}"); continue; }
            int job = t.Require_Job == 0 ? 1 : System.Numerics.BitOperations.TrailingZeroCount((ulong)t.Require_Job) + 1;
            var who = new EquipActor(255, job, t.Require_Sex is 1 or 2 ? t.Require_Sex : 1);
            foreach (var slot in worn)
            {
                var r = EquipRules.CanPlace(new InventoryItem { Id = 1 }, t, 1, slot, who, Empty);
                if (r != EquipRefusal.None) misses.Add($"{t.ID} slot {slot}: {r}");
            }
            checkedRows++;
        }
        Assert.True(checkedRows > 20000, "expected the bulk of ItemInfo to be checked");
        Assert.True(misses.Count == 0, string.Join("\n", misses.Take(25)) + $"\n({misses.Count} total)");
    }

    [Fact]
    public void RealLevel50SwordRowRespectsLevelAndJob()
    {
        var sword = LoadItemInfo().First(t => t.ID == 11100001u);
        Assert.Equal(6, sword.Equip_Slot);
        Assert.Equal(EquipRefusal.None, Place(sword, 1, 6, new EquipActor(50, 7, 1)));
        Assert.Equal(EquipRefusal.LevelTooLow, Place(sword, 1, 6, new EquipActor(49, 7, 1)));
        Assert.Equal(EquipRefusal.JobNotAllowed, Place(sword, 1, 6, new EquipActor(60, 1, 1)));
    }
}
