using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1348 ReqCraftListLearn: the profession-level gate, answered with string_ui 7690
/// "Your profession level is too low." (22622).
/// </summary>
public class CraftLearnLevelGateTests
{
    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes =
        new(() => BankBagSizeChange2345Tests.Table<CraftItemList>("Craft_ItemList.json"));

    private static CraftItemList Row(ushort id) => Recipes.Value.First(r => r.ID == id);

    [Fact]
    public void Recipe666_IsRefusedBelowItsCraftLevel_AndLearnedAtIt()
    {
        var row = Row(666);
        Assert.Equal((byte)1, row.ProType_ID);
        Assert.Equal((byte)50, row.Craft_Level);
        var known = CraftRecipeStore.KnownRecipeIds(Recipes.Value, 1, Array.Empty<ushort>());

        Assert.Equal(CraftRecipeStore.ResultProfessionLevelTooLow,
            CraftListLearnHandler.RefusalCode(row, 666, 1, known, 1_000_000, jobLevel: 49));
        Assert.NotNull(CraftListLearnHandler.RefusalReason(row, 666, 1, known, 1_000_000, jobLevel: 49));
        Assert.Equal(0, CraftListLearnHandler.RefusalCode(row, 666, 1, known, 1_000_000, jobLevel: 50));
        Assert.Null(CraftListLearnHandler.RefusalReason(row, 666, 1, known, 1_000_000, jobLevel: 50));

        
        Assert.Equal("040000005E580000", Convert.ToHexString(
            CraftRecipeStore.BuildAckCraftLearnRefusal2538(CraftRecipeStore.ResultProfessionLevelTooLow)));
    }

    [Fact]
    public void LevelGate_SitsAfterTheKnownCheck_AndBeforeTheFeeCheck()
    {
        var known = CraftRecipeStore.KnownRecipeIds(Recipes.Value, 1, Array.Empty<ushort>());
        var row = Row(452); 

        
        Assert.Equal(0, CraftListLearnHandler.RefusalCode(row, 452, 1, known, row.Learn_NeedMoney, jobLevel: 21));
        
        Assert.Equal(CraftRecipeStore.ResultProfessionLevelTooLow,
            CraftListLearnHandler.RefusalCode(row, 452, 1, known, 0, jobLevel: 20));
        
        Assert.Equal(CraftRecipeStore.ResultNotEnoughMoney,
            CraftListLearnHandler.RefusalCode(row, 452, 1, known, row.Learn_NeedMoney - 1, jobLevel: 21));
        
        Assert.Equal(CraftRecipeStore.ResultAlreadyLearned,
            CraftListLearnHandler.RefusalCode(row, 452, 1, known.Append((ushort)452).ToArray(), 1_000_000, jobLevel: 1));
        
        Assert.Equal(CraftRecipeStore.ResultWrongProfession,
            CraftListLearnHandler.RefusalCode(row, 452, 3, known, 1_000_000, jobLevel: 1));
    }
}
