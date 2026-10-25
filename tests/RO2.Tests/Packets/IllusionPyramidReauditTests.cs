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
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Illusion Pyramid combat notice, death close, and the 2381 refusal of a trade (1351) with a player who recently used the pyramid.
/// </summary>
public class IllusionPyramidReauditTests
{
    
 /// <summary>
 /// Armed by a 1616 since the last departure close: each combat notice sends 4d7101 and keeps the pyramid armed. The death close sends 547101 and ends the use, so later notices send nothing.
 /// </summary>
    [Fact]
    public async Task PyramidCombatNotice_RepeatsWhileArmed_AndTheDeathCloseDisarms()
    {
        await using var net = await Harness.StartAsync(1);
        var session = net.Sessions[0];
        var player = new PlayerState { SessionId = session.SessionId, EntityId = 0x09491D11 };
        await using var services = new ServiceCollection().BuildServiceProvider();

        await IllusionPyramidService.NotifyCombatAsync(session, player, NullLogger.Instance, CancellationToken.None); 
        await IllusionPyramidService.NoteStateRequestAsync(player, CancellationToken.None);                       
        await IllusionPyramidService.NotifyCombatAsync(session, player, NullLogger.Instance, CancellationToken.None);
        await IllusionPyramidService.NotifyCombatAsync(session, player, NullLogger.Instance, CancellationToken.None);
        await IllusionPyramidService.CloseOnDeathAsync(session, player, services, NullLogger.Instance, CancellationToken.None);
        await IllusionPyramidService.NotifyCombatAsync(session, player, NullLogger.Instance, CancellationToken.None); 
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeActorMoveSpeed,
            TraitPackets.BuildActorMoveSpeed2497(0x09491D11, TraitPackets.PcModalWorldEntrySpeed));

        var r = net.Readers[0];
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "4d7101"), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "4d7101"), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.AnsIllusionPyramidFinish, "547101"), Hexed(await r.NextAsync()));
        Assert.Equal(ZoneS2COpcodes.OnChangeActorMoveSpeed, (await r.NextAsync()).Op);
        Assert.False(IllusionPyramidRunStore.For(player).UsedSinceLeave);
    }

    
 /// <summary>
 /// A 1351 naming a target whose pyramid request came within the last 3 seconds is answered with 2381 result 10, and the target is not prompted. Otherwise the request is accepted (result 0).
 /// </summary>
    [Fact]
    public async Task TradeOpen_ToAPlayerFreshFromThePyramid_IsRetails2381Result10()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        players.AddPlayer(sid, new PlayerState { SessionId = sid, EntityId = (sid << 8) | 0x11, MapId = 1100, MapLoaded = true });
        
        var target = new PlayerState { SessionId = 0x0B96A1, EntityId = 0x0B96A111, MapId = 1100, MapLoaded = true };
        players.AddPlayer(target.SessionId, target);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new TradeOpenHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TradeOpenHandler>.Instance, players, broadcast);
        var request = Convert.FromHexString("010c0c00b1e211b59d0b11a1960b");
        const string accepted = "010e0e00b2e2000000000000" + "11a1960b";

        Assert.False(IllusionPyramidRunStore.RecentlyUsed(target, Environment.TickCount64));
        await handler.HandleAsync(net.Sessions[0], request);
        Assert.Equal((ZoneS2COpcodes.OnOpenTradePC, accepted), Hexed(await net.Readers[0].NextAsync()));

        
        await IllusionPyramidService.NoteStateRequestAsync(target, CancellationToken.None);
        await handler.HandleAsync(net.Sessions[0], request);
        Assert.Equal((ZoneS2COpcodes.OnOpenTradePC, "010e0e00b2e2000000000a0011a1960b"), Hexed(await net.Readers[0].NextAsync()));

        var slot = IllusionPyramidRunStore.For(target);
        slot.LastRequestMs = Environment.TickCount64 - IllusionPyramidRunStore.TradeRefusalWindowMs + 500;
        Assert.True(IllusionPyramidRunStore.RecentlyUsed(target, Environment.TickCount64));
        slot.LastRequestMs = Environment.TickCount64 - IllusionPyramidRunStore.TradeRefusalWindowMs;
        Assert.False(IllusionPyramidRunStore.RecentlyUsed(target, Environment.TickCount64));
        await handler.HandleAsync(net.Sessions[0], request);
        Assert.Equal((ZoneS2COpcodes.OnOpenTradePC, accepted), Hexed(await net.Readers[0].NextAsync()));
    }
}
