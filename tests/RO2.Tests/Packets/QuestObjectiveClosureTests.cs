using System.Text.Json;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Where a quest's objective closes: the accept grants of a quest with several give-item rows (QuestAcceptHandler.AcceptGrants), and the kill that closes a kill quest (LootService.AllKillObjectivesMet), with the frames each one sends.
/// </summary>
public class QuestObjectiveClosureTests
{
    private static readonly Lazy<ILookup<uint, QuestAcceptHandler.QuestActionRow>> ActionRows = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Quest_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Quest_Info.json");
        var rows = JsonSerializer.Deserialize<List<QuestAcceptHandler.QuestActionRow>>(
            File.ReadAllBytes(Path.Combine(dir.FullName, "data", "Quest_Info.json")))!;
        return rows.ToLookup(r => r.Quest_ID);
    });

    private static IReadOnlyList<(uint ItemId, int Count)> Grants(uint questId)
        => QuestAcceptHandler.SelectAcceptGrants(ActionRows.Value[questId]);

    
    [Fact]
    public void AcceptGrants_EveryOnAcceptGiveRow_InRowOrder()
    {
        
        Assert.Equal(new[] { (40000975u, 1), (40000964u, 1) }, Grants(213010100));
        
        Assert.Equal(new[] { (40000966u, 1), (40000978u, 1) }, Grants(213010250));
        
        Assert.Equal(new[] { (70006005u, 1) }, Grants(123000049));
    }

    [Fact]
    public void AcceptGrants_SkipRowsThatDoNotRunAtAccept()
    {
        
        
        Assert.Empty(Grants(911110170));
        Assert.Empty(Grants(111150003));
        
        
        Assert.Equal(3, ActionRows.Value[213010100].Count());
        Assert.Equal(2, Grants(213010100).Count);
    }

    [Fact]
    public void AcceptGrants_ReadRowsAfterTheFirst()
    {
        
        Assert.Equal(new[] { (40001124u, 1) }, Grants(411210050));
    }

    [Fact]
    public void AcceptDeliverTicks_ReproduceRetailFrames()
    {
        
        Assert.Equal(Convert.FromHexString("01121200dae300000000b446b20cc45d62020100"),
            QuestNotifyPackets.BuildQuestAddItem2424(213010100, 40000964, 1));
        Assert.Equal(Convert.FromHexString("01101000bce300000000b446b20c00000000"),
            QuestProgressPackets.BuildQuestComplete2427(213010100));
        
        Assert.Equal(Convert.FromHexString("01121200dae3000000004a47b20cc65d62020100"),
            QuestNotifyPackets.BuildQuestAddItem2424(213010250, 40000966, 1));
        Assert.Equal(Convert.FromHexString("01121200dae3000000004a47b20cd25d62020100"),
            QuestNotifyPackets.BuildQuestAddItem2424(213010250, 40000978, 1));
    }

    
    private static QuestData Row(uint questId, byte completeType, uint value1, uint value2)
        => new() { Quest_ID = questId, Complete_Type = completeType, Complete_Type_Value1 = value1, Complete_Type_Value2 = value2 };

    [Fact]
    public void KillQuest_ClosesWhenItsOnlyRowReachesValue2()
    {
        
        var rows = new[] { Row(311000032, 2, 70070000, 1) };
        var player = new PlayerState();
        Assert.False(LootService.AllKillObjectivesMet(player, 311000032, rows));
        player.QuestKillProgress[311000032] = 1;
        Assert.True(LootService.AllKillObjectivesMet(player, 311000032, rows));
    }

    [Fact]
    public void KillQuest_WithANonKillObjective_IsNotClosedByKills()
    {
        
        
        var rows = new[] { Row(1, 2, 70070000, 1), Row(1, 4, 40000964, 1) };
        var player = new PlayerState();
        player.QuestKillProgress[1] = 1;
        Assert.False(LootService.AllKillObjectivesMet(player, 1, rows));
        
        Assert.False(LootService.AllKillObjectivesMet(player, 1, new[] { Row(1, 2, 0, 1) }));
    }

    [Fact]
    public void KillQuest_ClosureIsForgottenWithTheQuestProgress()
    {
        var player = new PlayerState();
        Assert.False(LootService.KillObjectiveClosed(player, 311000032));
        LootService.ResetExtraKillProgress(player, 311000032); 
        Assert.False(LootService.KillObjectiveClosed(player, 311000032));
    }

    [Fact]
    public void KillClosureAndAutoPayout_ReproduceRetailFrames()
    {
        
        Assert.Equal(Convert.FromHexString("01121200dbe300000000e07b8912f02e2d040100"),
            QuestProgressPackets.BuildQuestMonsterOrGo2425(311000032, 70070000, 1));
        Assert.Equal(Convert.FromHexString("01101000bce300000000e07b891200000000"),
            QuestProgressPackets.BuildQuestComplete2427(311000032));
        
        
        Assert.Equal(Convert.FromHexString("011c1c00bfe30000000000000000e07b891200000000000000004e000000"),
            QuestProgressPackets.BuildQuestPrize2429(311000032, 0, 0, 78));
    }
}
