using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Honing (RMI 1676) grinding lines reach the 2498/2848 stat sheet: a worn item's per-instance
/// <see cref="InventoryItem.GrindingJson"/> adds each line's stat through <see cref="StatService.ComputeEquipmentStats"/>,
/// so a honed item actually grants its stats and is not merely cosmetic. Uses the shipped data so the
/// Grinding_Abili index comes from the real table.
/// </summary>
[Collection(RO2.Tests.GameDataStoreLoadedCollection.Name)]
public class HoningStatBonusTests
{
    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadFullStoreAsync);

    /// <summary>A honed flat line (+roll bp) on a worn item raises exactly that stat by ceil(Normal_Val*(1+roll/10000)),
    /// over and above the bare item's own traits (the delta cancels the innate bonuses).</summary>
    [DataFact]
    public async Task AHonedFlatLine_AddsItsStatToTheSheet()
    {
        var store = await Store.Value;
        var row = store.GrindingAbili.First(a => a.Trait_Type == 1 && a.Normal_Val > 0);   // flat line
        var template = store.Items.Values.First(t => EquipRules.WornSlotsFor(t).Count > 0);
        short slot = (short)EquipRules.WornSlotsFor(template)[0];
        const short roll = 1000;   // +10% of this line's Normal_Val
        int expected = (int)Math.Ceiling(row.Normal_Val * (10000.0 + roll) / 10000.0);

        var bare = new InventoryItem { Id = 1, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot };
        var honed = new InventoryItem
        {
            Id = 2, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot,
            GrindingJson = GrindingLines.Serialize(new[] { new GrindingLine(row.Abili_ID, roll) }),
        };

        int bareFlat = StatService.ComputeEquipmentStats(store, new[] { bare }).FlatOf(row.Trait_ID);
        int honedFlat = StatService.ComputeEquipmentStats(store, new[] { honed }).FlatOf(row.Trait_ID);

        Assert.True(expected > 0);
        Assert.Equal(expected, honedFlat - bareFlat);
    }

    /// <summary>An item with no honing adds nothing beyond its innate traits (the honing path is a no-op for a plain item).</summary>
    [DataFact]
    public async Task AnUnhonedItem_AddsNothingExtra()
    {
        var store = await Store.Value;
        var template = store.Items.Values.First(t => EquipRules.WornSlotsFor(t).Count > 0);
        short slot = (short)EquipRules.WornSlotsFor(template)[0];
        var a = new InventoryItem { Id = 1, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot };
        var b = new InventoryItem { Id = 2, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot, GrindingJson = null };

        Assert.Equal(StatService.ComputeEquipmentStats(store, new[] { a }).FlatOf(0),
                     StatService.ComputeEquipmentStats(store, new[] { b }).FlatOf(0));
    }

    private static async Task<GameDataStore> LoadFullStoreAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Grinding_Abili.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Grinding_Abili.json");
    }
}
