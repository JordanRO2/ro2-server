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
/// Tests for who receives 2401 OnActorAutoHeal and 2497 OnChangeActorMoveSpeed.
/// Test vectors are frame bodies; shipped tables are located by walking up from the test output directory.
/// </summary>
public class ActorVitalsBroadcastReauditTests
{
    
 /// <summary>Vitals broadcast for a PC at HP 1106/1106 and SP 225/225.</summary>
    private const string Retail2401 = "011c1c00ebe211c7eb0611c7eb0652040000e100000052040000e1000000";

    [Fact]
    public void ActorAutoHeal2401_IsTheArchiveFrame()
    {
        Assert.Equal(Retail2401, Hex(ActorVitalsPackets.BuildActorAutoHeal2401(0x06EBC711, 1106, 225, 1106, 225)));
    }

 /// <summary>
 /// ActorVitalsBroadcast (regeneration tick, healing, revival) reaches the owner and the viewers on the same map
 /// with the same frame, and nobody on another map.
 /// </summary>
    [Fact]
    public async Task ActorAutoHealBroadcast_ReachesTheOwnerAndItsViewers_NotAnotherMap()
    {
        await using var net = await Harness.StartAsync(3);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var owner = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x06EBC711, MapId = 1100,
            Hp = 1106, MaxHp = 1106, Mp = 225, MaxMp = 225 };
        var viewer = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x06EBC811, MapId = 1100 };
        var elsewhere = new PlayerState { SessionId = net.Sessions[2].SessionId, EntityId = 0x06EBC911, MapId = 1200 };
        players.AddPlayer(owner.SessionId, owner);
        players.AddPlayer(viewer.SessionId, viewer);
        players.AddPlayer(elsewhere.SessionId, elsewhere);

        await ActorVitalsBroadcast.SendAutoHealAsync(broadcast, owner);
        
        await net.Sessions[2].SendInternalRmiAsync(ZoneS2COpcodes.OnChangeActorMoveSpeed,
            TraitPackets.BuildActorMoveSpeed2497(0x06EBC911, TraitPackets.PcModalWorldEntrySpeed));

        Assert.Equal((ZoneS2COpcodes.OnActorAutoHeal, Retail2401), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnActorAutoHeal, Retail2401), Hexed(await net.Readers[1].NextAsync()));
        Assert.Equal(ZoneS2COpcodes.OnChangeActorMoveSpeed, (await net.Readers[2].NextAsync()).Op);
    }

    
 /// <summary>
 /// After a PC's 2504 and 2315, the viewer is sent that PC's speed (6.0). A PC on foot is restated at the speed set
 /// at world entry (<see cref="TraitPackets.PcModalWorldEntrySpeed"/>).
 /// </summary>
    [Fact]
    public void MoveSpeed2497_OfAPcOnFoot_IsTheFrameRetailSendsAViewerAfterThe2504()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var session = new ProudSession(socket, 0xA0B10001u, NullLogger.Instance);
        var pc = new PlayerState { SessionId = session.SessionId, EntityId = 0x06EF5A11, MapId = 1100 };

        Assert.Equal(6.0f, ActorMoveSpeed.CurrentSpeedOf(session, pc, new GameDataStore()));
        Assert.Equal("01101000fce300000000115aef060000c040", Hex(ActorMoveSpeed.BuildFor(session, pc, new GameDataStore())));
    }
}
