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
/// The PC record's party bit (2464/2504) and the 2358 pair sent when a leave disbands the party.
/// </summary>
public class PartyRecordAndLeaveReauditTests
{
    
 /// <summary>
 /// A PC in a party carries optMask 0x0004 and its party handle (tag 0x15) right after the mask, before the mount and
 /// the relation. This server sends no ProJob or title, so its mask is 0x0284. Outside the party, or for a PlayerState
 /// the party's manager does not hold, the record stays 0x0280.
 /// </summary>
    [Fact]
    public void PcRecord_OfAPartyMember_CarriesThePartyHandleUnderBit0004()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var rider = new PlayerState { SessionId = 7101, EntityId = 0x06EF5A11, CharacterId = 601, CharacterUid = 0x0DE0B6B3A7000601UL,
            Name = "Rider", Level = 40, MapId = 1100 };
        var mate = new PlayerState { SessionId = 7102, EntityId = 0x06EF5B11, CharacterId = 602, CharacterUid = 0x0DE0B6B3A7000602UL,
            Name = "Mate", Level = 40, MapId = 1100 };
        players.AddPlayer(rider.SessionId, rider);
        players.AddPlayer(mate.SessionId, mate);
        var character = new Character { Id = 601, Name = "Rider", Job = 1, Level = 40, AllowAnyoneReview = true };

        string Record(PlayerState who)
        {
            using var w = new RO2.Network.Packets.PacketWriter();
            PlayerSpawnService.WriteRecord(w, who, character, TestEmblems.Create(), null, 0x2513);
            return Hex(w.ToArray());
        }

        Assert.Contains("8002" + "13250000" + "02000000", Record(rider));       
        var party = new PartyState { PartyId = 0xFF1, LeaderSessionId = rider.SessionId, MemberSessionIds = [rider.SessionId, mate.SessionId] };
        party.Members[rider.SessionId] = rider;
        party.Members[mate.SessionId] = mate;
        parties.Track(party);
        Assert.Equal(0x000FF115u, PcRecordParty.HandleOf(rider));
        Assert.Contains("8402" + "15f10f00" + "13250000" + "02000000", Record(rider));
        
        Assert.Contains("8002" + "13250000" + "02000000", Record(new PlayerState { SessionId = rider.SessionId, EntityId = rider.EntityId, Name = "Rider" }));
    }

    
    
    
    private const string Archive2360Leave = "0115150034e30000000015690200ac9cd4a7b3b6e00d00";
    private const string Archive2358 = "010c0c0032e30000000015690200";

 /// <summary>
 /// A leave that leaves one member sends that member 2360, then two 2358 frames, matching the reference three-frame sequence.
 /// </summary>
    [Fact]
    public async Task LeaveThatDisbands_Sends2360_ThenTwo2358s()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var stayer = new PlayerState { SessionId = net.Sessions[0].SessionId, Name = "Stayer", CharacterUid = 0x0DE0B6B3A7000001UL, EntityId = 0x0A000111 };
        var leaver = new PlayerState { SessionId = net.Sessions[1].SessionId, Name = "Leaver", CharacterUid = 0x0DE0B6B3A7D49CACUL, EntityId = 0x0A000211 };
        players.AddPlayer(stayer.SessionId, stayer);
        players.AddPlayer(leaver.SessionId, leaver);
        var parties = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var party = new PartyState { PartyId = 0x269, LeaderSessionId = stayer.SessionId, MemberSessionIds = [stayer.SessionId, leaver.SessionId] };
        party.Members[stayer.SessionId] = stayer;
        party.Members[leaver.SessionId] = leaver;
        party.FrameSlots[stayer.SessionId] = 1;
        party.FrameSlots[leaver.SessionId] = 2;
        parties.Track(party);

        await parties.LeavePartyAsync(leaver.SessionId, PartyLeaveReason.Left);
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        Assert.Equal((ZoneS2COpcodes.OnPartyNotifyOut, Archive2360Leave), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnPartyNotifyDestroy, Archive2358), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnPartyNotifyDestroy, Archive2358), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Null(parties.GetPlayerParty(stayer.SessionId));
    }
}
