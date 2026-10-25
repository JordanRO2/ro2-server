using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Effects of a 1412 ReqFunctionObjectControlReward after the control: the FuncAction dispatch (portal,
/// dungeon entrance), the FuncReward roll, and the 2519, 2521 and 2741 bodies.
/// </summary>
/// <remarks>
/// Expected bodies are reference frames. The shipped data/ tables are located by walking up from the
/// test output (WorldEventTrigger3030Tests.Table).
/// </remarks>
public class FuncObjectActionsTests
{
    private static readonly Lazy<List<FuncAction>> FuncActionTable =
        new(() => WorldEventTrigger3030Tests.Table<FuncAction>("FuncAction.json"));

    private static readonly Lazy<List<FuncReward>> FuncRewardTable =
        new(() => WorldEventTrigger3030Tests.Table<FuncReward>("FuncReward.json"));

    private static readonly Lazy<List<PositionData>> PositionTable =
        new(() => WorldEventTrigger3030Tests.Table<PositionData>("Position_Info.json"));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void DungeonEntrance_ResolvesToItsGroup_AndThe2741IsRetails()
    {
        
        
        var action = FuncObjectActions.Resolve(FuncObjectActions.IndexActions(FuncActionTable.Value), 80010160);
        Assert.Equal(new FuncObjectAction(FuncObjectActionKind.DungeonEntrance, 2), action);
        Assert.Equal("01101000fae30000000014fe030000000000", Hex(NpcRewardHandler.BuildFuncObjectEnd2521(0x0003FE14u, 0)));
        Assert.Equal("0200000002000000", Hex(FuncObjectRewardPackets.BuildDungeonLevelSelect2741(action.Value)));
    }

    [Fact]
    public void Portal_ResolvesToAPositionInfoRow_AndThe2521IsRetails()
    {
        
        
        var byId = FuncObjectActions.IndexActions(FuncActionTable.Value);
        var action = FuncObjectActions.Resolve(byId, 80010600);
        Assert.Equal(FuncObjectActionKind.Portal, action.Kind);
        Assert.Equal(byId[80010600].Action_Value1, action.Value);
        Assert.Contains(PositionTable.Value, p => p.Position_ID == action.Value);
        Assert.Equal("01101000fae30000000014c9110000000000", Hex(NpcRewardHandler.BuildFuncObjectEnd2521(0x0011C914u, 0)));
    }

    [Fact]
    public void EveryPortalRow_NamesAPositionInfoRow()
    {
        var positions = PositionTable.Value.Select(p => p.Position_ID).ToHashSet();
        var portals = FuncActionTable.Value.Where(r => r.Action_Type == FuncObjectActions.PortalActionType).ToList();
        Assert.Equal(131, portals.Count);
        Assert.All(portals, r => Assert.Contains(r.Action_Value1, positions));
    }

    [Theory]
    [InlineData(80012130u)]   
    [InlineData(80372000u)]   
    [InlineData(80010960u)]   
    [InlineData(1u)]          
    public void OtherKinds_CarryNoAction(uint templateId)
        => Assert.Equal(FuncObjectActionKind.None,
            FuncObjectActions.Resolve(FuncObjectActions.IndexActions(FuncActionTable.Value), templateId).Kind);

    [Fact]
    public void GatherEnd_2519And2521_AreRetails()
    {
        
        Assert.Equal("01161600f8e30000000014b7080000000000000000000000",
            Hex(FuncObjectRewardPackets.BuildRewardResult2519(0x0008B714u, 0)));
        Assert.Equal("01101000fae30000000014b7080000000000", Hex(NpcRewardHandler.BuildFuncObjectEnd2521(0x0008B714u, 0)));
    }

    [Fact]
    public void BagFull_2519And2521_CarryRetails22208()
    {
        
        Assert.Equal("01161600f8e30000000014f50300c0560000000000000000",
            Hex(FuncObjectRewardPackets.BuildRewardResult2519(0x0003F514u, FuncObjectRewardPackets.BagFullMessageId)));
        Assert.Equal("01101000fae30000000014f50300c0560000",
            Hex(NpcRewardHandler.BuildFuncObjectEnd2521(0x0003F514u, FuncObjectRewardPackets.BagFullMessageId)));
    }

    [Fact]
    public void Roll_OfTheOneRowNode_PaysItsItemOnce()
    {
        
        var rows = FuncObjectActions.IndexRewards(FuncRewardTable.Value)[80012130];
        Assert.Equal(new[] { (40001252u, 1) }, FuncObjectActions.Roll(rows, new Random(1)));
    }

    [Fact]
    public void Roll_DrawsEachRowOnItsRate_AndTheCountOverMinToMax()
    {
        
        
        var rows = new[]
        {
            new FuncReward { ID = 1, Item_ID = 10, Min_Num = 1, Max_Num = 1, Rate = 10000 },
            new FuncReward { ID = 1, Item_ID = 11, Min_Num = 1, Max_Num = 2, Rate = 7000 },
            new FuncReward { ID = 1, Item_ID = 12, Min_Num = 1, Max_Num = 2, Rate = 3000 },
            new FuncReward { ID = 1, Item_ID = 13, Min_Num = 1, Max_Num = 1, Rate = 0 },
        };
        var random = new Random(20260809);
        const int draws = 20_000;
        var paid = new Dictionary<uint, int>();
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < draws; i++)
        {
            foreach (var (item, count) in FuncObjectActions.Roll(rows, random))
            {
                paid[item] = paid.GetValueOrDefault(item) + 1;
                if (item == 11) counts[count] = counts.GetValueOrDefault(count) + 1;
                Assert.InRange(count, 1, item == 10 ? 1 : 2);
            }
        }
        Assert.Equal(draws, paid[10]);
        Assert.InRange(paid[11] / (double)draws, 0.68, 0.72);
        Assert.InRange(paid[12] / (double)draws, 0.28, 0.32);
        Assert.False(paid.ContainsKey(13));
        Assert.InRange(counts[1] / (double)paid[11], 0.47, 0.53);
    }

    [Fact]
    public void EveryRewardGroup_NamesDistinctItems()
    {
        
        
        Assert.All(FuncObjectActions.IndexRewards(FuncRewardTable.Value).Values,
            rows => Assert.Equal(rows.Length, rows.Select(r => r.Item_ID).Distinct().Count()));
    }
}
