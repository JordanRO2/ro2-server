using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the per-hit aggro update (2630) and the end of an NPC control (2519, 2521).
/// </summary>
public class NpcThreatAndControlReauditTests
{
    
 /// <summary>
 /// Every hit with an attacker queues a 2630 carrying the attacker's new threat and the current target, not
 /// only the first hit. The killing hit's step is queued as well; the NPC combat service drops it because the
 /// monster is dead.
 /// </summary>
    [Fact]
    public void AggroChange_IsQueuedOnEveryHit_WithTheRunningThreat()
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var spawn = new SpawnEntry { MapId = 1300, Index = 383, PosX = 447.5f, PosY = 321.1f, PosZ = 12.5f, AssetIndex = 28580000 };
        npcs.TrackSpawns(spawn.MapId, [spawn]);
        var handle = npcs.AllocateHandle(spawn.MapId, spawn.Index);
        var npc = npcs.GetNpc(handle)!;
        npc.Camp = 2;
        npc.MaxHp = 100;
        npc.Hp = 100;
        const uint pc = 118442769;   

        npcs.ApplyDamage(handle, 10, pc, "Carl3ee");
        npcs.ApplyDamage(handle, 15, pc, "Carl3ee");
        npcs.ApplyDamage(handle, 20, pc, "Carl3ee");
        var changes = new List<NpcStateManager.AggroChange>();
        while (npcs.TryDequeueAggroChange(out var ch)) changes.Add(ch);

        Assert.Equal(new uint[] { 10, 25, 45 }, changes.Select(c => c.Threat).ToArray());
        Assert.All(changes, c => Assert.Equal((handle, pc, pc, "Carl3ee"), (c.NpcHandle, c.CurrentTarget, c.PcHandle, c.PcName)));
        Assert.Equal(22 + 2 * "Carl3ee".Length, AggroPackets.BuildAggroChanged2630(handle, pc, pc, "Carl3ee", 45).Length);

        Assert.True(npcs.ApplyDamage(handle, 1000, pc, "Carl3ee").Died);
        Assert.True(npcs.TryDequeueAggroChange(out var killStep));
        Assert.True(npcs.GetNpc(killStep.NpcHandle) is not { IsDead: false });   
    }

    
 /// <summary>The 2521 that ends an NPC control, and the prefix of a 2519 code 0 for a second NPC.</summary>
    private const string Live2521Npc = "01101000fae300000000135f490400000000";
    private const string Live2519NpcPrefix = "01161600f8e30000000013334904000000000000";

 /// <summary>
 /// A 1412 that ends a running 1410 control of an NPC (pose 0x14) takes the pose kinds' path: a state-0 reset 2398
 /// to the controller and nearby players, a 2519 code 0 (no reward payout for an NPC), then a 2521 code 0.
 /// </summary>
    [Fact]
    public async Task NpcReward_OnARunningNpcControl_ResetsThePose_Then2519_Then2521()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        const uint pc = 0x0EC50F11, npc = 0x04495F13;
        var controller = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = pc, MapId = 1100, MapLoaded = true };
        var onlooker = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x0EC51011, MapId = 1100, MapLoaded = true };
        players.AddPlayer(controller.SessionId, controller);
        players.AddPlayer(onlooker.SessionId, onlooker);
        var collection = new ServiceCollection();
        collection.AddSingleton(new GameDataStore());
        await using var services = collection.BuildServiceProvider();
        var handler = new NpcRewardHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NpcRewardHandler>.Instance, players, broadcast);
        var store = services.GetRequiredService<GameDataStore>();
        Assert.Equal(FuncObjectControlPackets.NpcControlState, FuncObjectControlPackets.ControlStateFor(store, 1100, npc));

        
        FuncObjectControlState.Begin(controller, new FuncObjectControl(npc, FuncObjectControlPackets.NpcControlState));
        
        var request = Convert.FromHexString("010c0400f7e3" + "110fc50e" + "135f4904");
        await handler.HandleAsync(net.Sessions[0], request);

        var reset = Hex(FuncObjectControlPackets.BuildControlReset2398(pc, npc));
        Assert.Equal((ZoneS2COpcodes.OnChangeState, reset), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnFuncObjectRewardResult, Hex(FuncObjectRewardPackets.BuildRewardResult2519(npc, 0))),
            Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnFuncObjectError, Live2521Npc), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnChangeState, reset), Hexed(await net.Readers[1].NextAsync()));
        Assert.StartsWith(Live2519NpcPrefix, Hex(FuncObjectRewardPackets.BuildRewardResult2519(0x04493313, 0)));

        
        await handler.HandleAsync(net.Sessions[0], request);
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
    }
}
