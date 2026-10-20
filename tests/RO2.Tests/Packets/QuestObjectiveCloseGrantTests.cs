using System.Text.Json;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Give-item rows that run when a quest's objective closes (Quest_Info Active_Type 3). QuestPrizeHandler.GrantObjectiveCloseItemsAsync pays them right after the quest's 2427. The turn-ins that close an objective are answered with 2432, 2427, one 2333 per give-item row, 2429 and 2435, in that order.
/// </summary>
public class QuestObjectiveCloseGrantTests
{
 /// <summary>The Quest_Info columns these tests read: the action columns and the objective type.</summary>
    private sealed record Row(uint Quest_ID, byte Active_Type, byte Action_Type, uint Action_Value1, uint Action_Value2,
        byte Complete_Type);

    private static readonly Lazy<ILookup<uint, Row>> Rows = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Quest_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Quest_Info.json");
        var rows = JsonSerializer.Deserialize<List<Row>>(
            File.ReadAllBytes(Path.Combine(dir.FullName, "data", "Quest_Info.json")))!;
        return rows.ToLookup(r => r.Quest_ID);
    });

    private static IEnumerable<QuestAcceptHandler.QuestActionRow> ActionRows(uint questId)
        => Rows.Value[questId].Select(r => new QuestAcceptHandler.QuestActionRow(
            r.Quest_ID, r.Active_Type, r.Action_Type, r.Action_Value1, r.Action_Value2));

    [Fact]
    public void ObjectiveCloseGrants_AreTheActiveType3GiveRows()
    {
        
        Assert.Equal(new[] { (21330015u, 1) }, QuestAcceptHandler.SelectObjectiveCloseGrants(ActionRows(911110170)));
        
        Assert.Equal(new[] { (40001250u, 1) }, QuestAcceptHandler.SelectObjectiveCloseGrants(ActionRows(111150003)));
        Assert.Equal(new[] { (40001263u, 1) }, QuestAcceptHandler.SelectObjectiveCloseGrants(ActionRows(911110820)));
    }

    [Fact]
    public void ObjectiveCloseGrants_AreNotAcceptGrants_AndTheReverse()
    {
        
        Assert.Empty(QuestAcceptHandler.SelectAcceptGrants(ActionRows(911110170)));
        
        Assert.Empty(QuestAcceptHandler.SelectObjectiveCloseGrants(ActionRows(213010100)));
        Assert.Equal(2, QuestAcceptHandler.SelectAcceptGrants(ActionRows(213010100)).Count);
    }

    [Fact]
    public void ObjectiveCloseGrants_OnlyOnTalkAndCollectQuests()
    {
        
        
        
        var withGrant = Rows.Value
            .Where(g => QuestAcceptHandler.SelectObjectiveCloseGrants(ActionRows(g.Key)).Count > 0)
            .ToList();
        Assert.Equal(new uint[] { 111150003, 911110170, 911110820 }, withGrant.Select(g => g.Key).OrderBy(q => q));
        Assert.All(withGrant, g => Assert.All(g.Where(r => r.Complete_Type != 0),
            r => Assert.Contains(r.Complete_Type, new byte[] { 1, 4 })));
    }
}
