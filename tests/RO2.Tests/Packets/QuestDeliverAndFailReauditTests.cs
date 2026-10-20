using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Deliver-item progress ticks (2428) and the death and forbidden-kill quest failures (2434).
/// </summary>
public class QuestDeliverAndFailReauditTests
{
    
 /// <summary>
 /// Deliver quests tick to 0 when their item leaves the bag. QuestCollectDropTicks indexes type-5 rows next to the type-4 ones and plans these bodies.
 /// </summary>
    [Fact]
    public async Task DeliverItemLeavingTheBag_TicksTheLiveFrames()
    {
        var store = await LoadStoreAsync("Quest_Info.json");
        var held = new HashSet<uint> { 111010090, 211111030, 211110670, 123000024 };
        string Tick(uint item) => string.Join(",", QuestCollectDropTicks.PlanTicks(
                QuestCollectDropTicks.RowsCollecting(store.QuestRows, item), held, 0)
            .Select(t => Hex(QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(t.QuestId, item, t.Count))));

        Assert.Equal("01121200bde3000000002ae19d061b5c62020000", Tick(40000539));
        Assert.Equal("01121200bde300000000764c950c2d5b62020000", Tick(40000301));
        Assert.Equal("01121200bde3000000000e4b950c715a62020000", Tick(40000113));
        
        Assert.Empty(QuestCollectDropTicks.PlanTicks(QuestCollectDropTicks.RowsCollecting(store.QuestRows, 40000539),
            new HashSet<uint> { 211111030 }, 0));
    }

 /// <summary>The first-row counter is a u16 (2425) lowered without narrowing. A deliver quest's counter is lowered like a collect quest's, so its turn-in reads the objective as open again.</summary>
    [Fact]
    public void DropTick_LowersTheCounterWithoutNarrowing()
    {
        var player = new PlayerState();
        player.QuestKillProgress[911000440] = 300;
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(911000440, 0, 280));
        Assert.Equal((ushort)280, player.QuestKillProgress[911000440]);

        player.QuestKillProgress[111010090] = 1;
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(111010090, 0, 0));
        Assert.Equal((ushort)0, player.QuestKillProgress[111010090]);
    }

    
 /// <summary>
 /// Quest-fail frames with the u16 value 21714: Fail_Type 39 (escort), Fail_Type 26 (tail 0) and Fail_Type 2 (NPC 50001250).
 /// </summary>
    [Fact]
    public void QuestFailed2434_AreTheRetailFrames()
    {
        Assert.Equal(21714, QuestFailPackets.FailedCode);
        Assert.Equal("1e23ad06d25427e418d400",
            Hex(QuestFailPackets.BuildQuestFailed2434(112010014, QuestFailPackets.FailedCode, 39, 13900004)));
        Assert.Equal("daeec40dd2541a00000000",
            Hex(QuestFailPackets.BuildQuestFailed2434(231010010, QuestFailPackets.FailedCode, QuestFailTriggers.FailTypeDeath, QuestFailTriggers.DeathTail)));
        Assert.Equal("a046b20cd2540262f5fa02",
            Hex(QuestFailPackets.BuildQuestFailed2434(213010080, QuestFailPackets.FailedCode, QuestFailTriggers.FailTypeKill, 50001250)));
    }

    private static IReadOnlyDictionary<uint, QuestFailTriggers.FailRow[]> ShippedFailRows() =>
        QuestFailTriggers.ParseFailRows(File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Quest_Info.json")));

 /// <summary>A death fails each held Fail_Type 26 quest once, in ascending order, with tail 0. A quest without the row, or one already failed, is left alone.</summary>
    [Fact]
    public void Death_FailsHeldFailType26Quests()
    {
        var rows = ShippedFailRows();
        var plan = QuestFailTriggers.PlanDeathFailures(rows, new uint[] { 231010010, 111010090, 112116700 }, q => q == 112116700);
        var only = Assert.Single(plan);
        Assert.Equal("daeec40dd2541a00000000", Hex(QuestFailPackets.BuildQuestFailed2434(
            only.QuestId, QuestFailPackets.FailedCode, only.FailType, only.Tail)));
        Assert.Empty(QuestFailTriggers.PlanDeathFailures(rows, new uint[] { 213010080 }, _ => false));
    }

 /// <summary>Killing the NPC that a held Fail_Type 2 row forbids fails the quest at that kill, with the NPC as the tail. Killing another NPC counts nothing, and a quest is failed once.</summary>
    [Fact]
    public void ForbiddenKill_FailsTheQuestAtItsCount()
    {
        var rows = ShippedFailRows();
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<(uint Quest, int Row), int>();
        var failed = new HashSet<uint>();
        Assert.Empty(QuestFailTriggers.CountKill(rows, new uint[] { 213010080 }, failed.Contains, counts, 50001251));
        var plan = QuestFailTriggers.CountKill(rows, new uint[] { 213010080, 231010010 }, failed.Contains, counts, 50001250);
        var f = Assert.Single(plan);
        Assert.Equal("a046b20cd2540262f5fa02", Hex(QuestFailPackets.BuildQuestFailed2434(
            f.QuestId, QuestFailPackets.FailedCode, f.FailType, f.Tail)));
        failed.Add(f.QuestId);
        Assert.Empty(QuestFailTriggers.CountKill(rows, new uint[] { 213010080 }, failed.Contains, counts, 50001250));
        
        Assert.Empty(QuestFailTriggers.CountKill(rows, new uint[] { 212000040 }, _ => false, counts, 0));
    }
}
