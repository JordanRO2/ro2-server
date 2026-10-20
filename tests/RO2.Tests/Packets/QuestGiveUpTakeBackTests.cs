using System.Text.Json;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Quest give-up (1375 ReqQuestGiveUp) takes back the items the quest handed out. QuestGiveUpHandler walks QuestAcceptHandler.AcceptGrants and sends one 2379 per give-item row (Active_Type 1) still in the bag, in Quest_Info row order, then 2420. Each row is answered with its 2379 (count 0), and the give-up ends with 2420 result 0.
/// </summary>
public class QuestGiveUpTakeBackTests
{
    private sealed record Row(uint Quest_ID, byte Active_Type, byte Action_Type, uint Action_Value1, uint Action_Value2);

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
    public void ATwoRowQuest_GivesBackBothItems_InRetailsOrder()
    {
        
        Assert.Equal(new[] { (40000966u, 1), (40000978u, 1) },
            QuestAcceptHandler.SelectAcceptGrants(ActionRows(213010250)));
    }

    [Fact]
    public void AnActiveType3GiveRow_IsNotTakenBack()
    {
        
        
        Assert.Empty(QuestAcceptHandler.SelectAcceptGrants(ActionRows(111150003)));
    }

    [Fact]
    public void NoQuestNamesOneItemInTwoGiveRows_SoOneBagReadServesEveryRow()
    {
        
        
        foreach (var quest in Rows.Value)
        {
            var items = QuestAcceptHandler.SelectAcceptGrants(ActionRows(quest.Key)).Select(g => g.ItemId).ToList();
            Assert.Equal(items.Count, items.Distinct().Count());
        }
    }
}
