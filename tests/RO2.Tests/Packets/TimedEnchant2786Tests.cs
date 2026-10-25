using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// The timed enchants of 1543 ReqAttachSocket (<see cref="TimedEnchant"/>): who takes one, what it writes into the
/// 2786 record, how its timer runs, and that its bonus counts only while it is live. Rows are the shipped
/// data/ItemReinforceInfo.json and ItemReinforceTraitInfo.json.
/// </summary>
public class TimedEnchant2786Tests
{
    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-timedenchant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "ItemInfo.json", "ItemReinforceInfo.json", "ItemReinforceTraitInfo.json" })
                File.Copy(ItemLookTestData.File(name), Path.Combine(dir, name));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ThereAre38TimedRows_OneRowPerSource_AllWithTraitLines()
    {
        var store = await Store.Value;
        var timed = store.ItemReinforceInfo.Where(r => r.Reinforce_Type == TimedEnchant.TimedReinforceType).ToList();
        Assert.Equal(38, timed.Count);
        foreach (var row in timed)
        {
            Assert.Same(row, TimedEnchant.RowOf(store, row.SourceItem_ID));
            Assert.Contains(store.ItemReinforceTraitInfo, t => t.Reinforce_ID == row.Reinforce_ID);
        }
    }

    [Fact]
    public async Task ARetailBlessing_WritesItsIdAndDuration_AtRecord17And19()
    {
        // A 1543 puts Reinforce_ID 30003 (Vulcan's blessing III, 22290033, Duration 300) on the worn dagger
        // 16702297; the resulting 2786 record carries the Reinforce_ID and the duration at record+17 and +19.
        var store = await Store.Value;
        var row = TimedEnchant.RowOf(store, 22290033)!;
        var dagger = new InventoryItem { Id = 0x0E043DA8065E61CC, ItemId = 16702297, Count = 1, StorageType = 1, Slot = 6, Durability = 9452 };
        Assert.True(TimedEnchant.Fits(row, store.GetItem(dagger.ItemId)!));
        TimedEnchant.Apply(dagger, row);
        var record = WorldEntryService.BuildItemRecord126(dagger);
        Assert.Equal("cc615e06a83d040e59dbfe00ec2401000633752c01", Convert.ToHexString(record.AsSpan(0, 21)).ToLowerInvariant());
    }

    [Fact]
    public async Task ABlessingFits_ByItsPartsBit_AndTheTargetsLevel()
    {
        var store = await Store.Value;
        var vulcan = TimedEnchant.RowOf(store, 22290033)!;   // parts 0x060000C0: slots 6, 7, 25, 26
        var balial = TimedEnchant.RowOf(store, 22290036)!;   // parts 0x0600003E: slots 1-5, 25, 26
        var dagger = store.GetItem(16702297)!;               // Equip_Slot 6, Require_Level 50
        var chefTop = store.GetItem(12611000)!;              // Equip_Slot 2, Require_Level 0
        Assert.True(TimedEnchant.Fits(vulcan, dagger));
        Assert.False(TimedEnchant.Fits(balial, dagger));
        // The slot bit fits, but its Require_Level 0 is under every row's TargetItem_ReqLevel 1.
        Assert.Equal(1, balial.TargetItem_ReqLevel);
        Assert.False(TimedEnchant.Fits(balial, chefTop));
        Assert.False(TimedEnchant.Fits(vulcan, new ItemData { ID = 1, Equip_Slot = 38, Require_Level = 50 })); // 38 & 31 = 6
    }

    [Theory]
    [InlineData((ushort)300, (ushort)290)]
    [InlineData((ushort)10, (ushort)0)]
    [InlineData((ushort)5, (ushort)0)]
    [InlineData((ushort)0, (ushort)0)]
    public void ATickTakes10Seconds_DownToZero(ushort before, ushort after)
        => Assert.Equal(after, TimedEnchant.AfterTick(before));

    [Fact]
    public async Task TheBonus_CountsOnlyWhileTheEnchantIsLiveOnAWornItem()
    {
        // 10014 = Tiger Embroidery II: Kind 1, Type 0 (STR), Value 2. Template 12611002 is Equip_Slot 2 with no
        // TraitInfo row (a real, store-known template, so the INV-H2 stat cap counts it), so the enchant is the only
        // thing it adds.
        var store = await Store.Value;
        var row = TimedEnchant.RowOf(store, 22290014)!;
        Assert.Equal(10014, row.Reinforce_ID);
        InventoryItem Item(byte storage, ushort seconds) => new()
        {
            Id = 5, ItemId = 12611002, Count = 1, StorageType = storage, Slot = 2,
            EnchantReinforceId = row.Reinforce_ID, EnchantSeconds = seconds,
        };
        int Str(InventoryItem it) => StatService.ComputeEquipmentStats(store, new[] { it }).FlatOf(0);
        var none = new InventoryItem { Id = 5, ItemId = 12611002, Count = 1, StorageType = 1, Slot = 2 };
        Assert.Equal(2, Str(Item(1, 2400)) - Str(none));
        Assert.Equal(0, Str(Item(1, 0)) - Str(none));    // spent: the id stays on the record, the bonus is gone
        Assert.Equal(0, Str(Item(0, 2400)) - Str(none)); // in a bag
    }

    [Fact]
    public void ApplyingAgain_RestartsAtTheFullDuration_AndReplacesAnother()
    {
        var item = new InventoryItem { EnchantReinforceId = 30003, EnchantSeconds = 40 };
        TimedEnchant.Apply(item, new RO2.GameData.Tables.ItemReinforceInfo { Reinforce_ID = 30003, Duration = 300 });
        Assert.Equal(((ushort)30003, (ushort)300), (item.EnchantReinforceId, item.EnchantSeconds));
        TimedEnchant.Apply(item, new RO2.GameData.Tables.ItemReinforceInfo { Reinforce_ID = 10001, Duration = 1800 });
        Assert.Equal(((ushort)10001, (ushort)1800), (item.EnchantReinforceId, item.EnchantSeconds));
        Assert.True(TimedEnchant.IsLive(item));
        Assert.False(TimedEnchant.IsLive(new InventoryItem { EnchantReinforceId = 10001, EnchantSeconds = 0 }));
    }
}
