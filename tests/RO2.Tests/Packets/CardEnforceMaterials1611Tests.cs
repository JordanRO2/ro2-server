using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the material rule of 1611 ReqCardEnforceItem (<see cref="CardEnforceItemHandler.MaterialFits"/>):
/// which items the card window accepts in each of its three material slots. Normal cards route materials by
/// Item_Type: 143 goes to the stone slot when the stack holds the row's MeterialCount, 144 and 146 to the
/// coating slot, and 145 or the synthesis stone of the card's grade to the catalyst slot. Legendary cards
/// also accept 140, 141/142 and 145/22300104. The rows use the shipped data (data/ItemInfo.json Item_Type,
/// data/CardInfo.json Grade_Type, data/CardCompose.json Card_Grade, data/CardEnforce.json grade-0 rows).
/// </summary>
public class CardEnforceMaterials1611Tests
{
 /// <summary>Queen Serenia Card [Epic+]: Item_Type 13, CardInfo Grade_Type 4, EnforceID 450.</summary>
    private const uint EpicCard = 22310892;

 /// <summary>Baphomet MVP Card - L1: Item_Type 139, CardInfo Grade_Type 5, EnforceID 550.</summary>
    private const uint LegendaryCard = 22320010;

 /// <summary>CardEnforce 450 grade 0: 20 x Card Enhanced Stone (22300201).</summary>
    private static readonly CardEnforce EpicRow = new() { EnforceID = 450, EnforceGrade = 0, MeterialID = 22300201, MeterialCount = 20 };

 /// <summary>CardEnforce 550 grade 0: 5 x Legendary Card Enhancement Stone (66600018).</summary>
    private static readonly CardEnforce LegendaryRow = new() { EnforceID = 550, EnforceGrade = 0, MeterialID = 66600018, MeterialCount = 5 };

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        
        
        var dir = Path.Combine(Path.GetTempPath(), "ro2-cardenforce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":22310892,"Item_Type":13},{"ID":22320010,"Item_Type":139},
                 {"ID":22300201,"Item_Type":143},{"ID":66600018,"Item_Type":140},
                 {"ID":66600000,"Item_Type":146},{"ID":66600006,"Item_Type":146},{"ID":70000164,"Item_Type":144},
                 {"ID":66600012,"Item_Type":142},{"ID":66600015,"Item_Type":141},
                 {"ID":66600002,"Item_Type":145},{"ID":66600005,"Item_Type":145},
                 {"ID":22300101,"Item_Type":21},{"ID":22300102,"Item_Type":21},{"ID":22300103,"Item_Type":21},
                 {"ID":22300104,"Item_Type":21},{"ID":22300401,"Item_Type":21},{"ID":60000617,"Item_Type":21},
                 {"ID":21330037,"Item_Type":68}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardInfo.json"), """
                [{"Card_ID":22310892,"Grade_Type":4},{"Card_ID":22320010,"Grade_Type":5}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardCompose.json"), """
                [{"ItemID":22300101,"Card_Grade":2},{"ItemID":22300102,"Card_Grade":3},{"ItemID":22300401,"Card_Grade":0},
                 {"ItemID":22300103,"Card_Grade":4},{"ItemID":22300104,"Card_Grade":5}]
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

    [Theory]
    
    [InlineData(22300201u, 20, true)]
    [InlineData(22300201u, 19, false)]
    
    [InlineData(21330037u, 255, false)]
    public async Task StoneSlot_TakesTheRowsMaterial_InItsCount(uint item, int count, bool fits)
    {
        var store = await Store.Value;
        Assert.Equal(fits, CardEnforceItemHandler.MaterialFits(store, EpicCard, EpicRow, CardEnforceItemHandler.StoneSlot, item, count));
    }

    [Theory]
    [InlineData(66600000u, true)]   
    [InlineData(70000164u, true)]   
    [InlineData(66600012u, false)]  
    [InlineData(66600002u, false)]  
    [InlineData(21330037u, false)]  
    public async Task CoatingSlot_OfANormalCard_Takes144Or146(uint item, bool fits)
    {
        var store = await Store.Value;
        Assert.Equal(fits, CardEnforceItemHandler.MaterialFits(store, EpicCard, EpicRow, CardEnforceItemHandler.CoatingSlot, item, 1));
    }

    [Theory]
    [InlineData(66600002u, true)]   
    [InlineData(22300103u, true)]   
    [InlineData(22300102u, false)]  
    [InlineData(22300401u, false)]  
    [InlineData(60000617u, false)]  
    [InlineData(66600000u, false)]  
    public async Task CatalystSlot_TakesALuckyPowder_OrTheStoneOfTheCardsGrade(uint item, bool fits)
    {
        var store = await Store.Value;
        Assert.Equal(fits, CardEnforceItemHandler.MaterialFits(store, EpicCard, EpicRow, CardEnforceItemHandler.CatalystSlot, item, 1));
    }

    [Theory]
    [InlineData(CardEnforceItemHandler.StoneSlot, 66600018u, 5, true)]
    [InlineData(CardEnforceItemHandler.StoneSlot, 22300201u, 20, false)]
    [InlineData(CardEnforceItemHandler.CoatingSlot, 66600012u, 1, true)]   
    [InlineData(CardEnforceItemHandler.CoatingSlot, 66600015u, 1, true)]   
    [InlineData(CardEnforceItemHandler.CoatingSlot, 66600000u, 1, false)]  
    [InlineData(CardEnforceItemHandler.CatalystSlot, 22300104u, 1, true)]  
    [InlineData(CardEnforceItemHandler.CatalystSlot, 22300103u, 1, false)]
    public async Task LegendaryCard_UsesTheLegendarySet(int slot, uint item, int count, bool fits)
    {
        var store = await Store.Value;
        Assert.Equal(fits, CardEnforceItemHandler.MaterialFits(store, LegendaryCard, LegendaryRow, slot, item, count));
    }

    [Fact]
    public async Task AnEmptyStack_FitsNoSlot()
    {
        var store = await Store.Value;
        Assert.False(CardEnforceItemHandler.MaterialFits(store, EpicCard, EpicRow, CardEnforceItemHandler.CoatingSlot, 66600000, 0));
        Assert.False(CardEnforceItemHandler.MaterialFits(store, EpicCard, EpicRow, CardEnforceItemHandler.CatalystSlot, 66600002, 0));
    }
}
