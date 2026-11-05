using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>An accessory's imprinted seal lines (signed, absolute) fold into the stat sheet, negatives included.</summary>
[Collection(RO2.Tests.GameDataStoreLoadedCollection.Name)]
public class SealStatBonusTests
{
    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadFullStoreAsync);

    [DataTheory]
    [InlineData(37)]
    [InlineData(-21)]
    public async Task ASealFlatLine_FoldsItsSignedDeltaIntoTheStat(int value)
    {
        var store = await Store.Value;
        var template = store.Items.Values.First(t => EquipRules.WornSlotsFor(t).Count > 0);
        short slot = (short)EquipRules.WornSlotsFor(template)[0];
        const byte statType = 0;   // STR

        var bare = new InventoryItem { Id = 1, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot };
        var sealed_ = new InventoryItem
        {
            Id = 2, ItemId = template.ID, Count = 1, StorageType = 1, Slot = slot,
            SealJson = SealLines.Serialize(new[] { new SealLine(1, statType, value) }),
        };

        int bareFlat = StatService.ComputeEquipmentStats(store, new[] { bare }).FlatOf(statType);
        int sealedFlat = StatService.ComputeEquipmentStats(store, new[] { sealed_ }).FlatOf(statType);

        Assert.Equal(value, sealedFlat - bareFlat);
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
