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
/// The 2391 head's talent pool and the 2903 that ends a level-up burst.
/// </summary>
/// <remarks>
/// Shipped tables are located by walking up from the test output directory.
/// </remarks>
public class DeadMeAndLevelUpReauditTests
{
    
 /// <summary>
 /// The remaining talent pool is not always zero: it equals the session's last 2310 value. A level-50
 /// victim with no talent skill learned carries 49 (StatService.TalentPointsRemaining). The frame keeps
 /// len = body - 4 and n = 0 when no gear is worn.
 /// </summary>
    [Fact]
    public async Task DeadMe2391_HeadCarriesTheLiveTalentPool()
    {
        await using var net = await Harness.StartAsync(1);
        var victim = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x06EBC711, CharacterId = 77,
            CharacterUid = 0x0DE0B6B3A7000077UL, Level = 50, MapId = 1100,
            Character = new Character { Id = 77, Name = "Victim", Level = 50 } };
        var store = new GameDataStore();
        Assert.Equal(49, StatService.TalentPointsRemaining(store, victim.Job, victim.Level, []));

        Assert.True(await PlayerDeathPackets.SendDeadMeAsync(net.Sessions[0], victim, new GearInventory([]), store));
        var (op, body) = await net.Readers[0].NextAsync();

        Assert.Equal(ZoneS2COpcodes.OnDeadMe, op);
        Assert.Equal(4 + 4 + 205 + 3, body.Length);
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0x06EBC711u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(50, body[8 + 69]);
        Assert.Equal(49, body[8 + 142]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8 + 205 + 1)));
        
        Assert.Equal(47, PlayerDeathPackets.BuildOnDeadMe2391(victim, [], 47)[8 + 142]);
    }

    
 /// <summary>The frame in which the Helper flag ends, the last frame of the lower member's level-up burst.</summary>
    private const string Retail2903 = "4c4feba7b3b6e00d00";

 /// <summary>
 /// A member's level-up that brings it within 10 levels of the level-50 Helper ends its burst with the 2903,
 /// followed by the roster with the flag cleared. The Helper itself gets only the roster. The flag is removed
 /// from the party. CombatService.SendLevelUpBurstAsync reaches PartyService through its scope factory.
 /// </summary>
    [Fact]
    public async Task LevelUp_ThatLapsesTheHelper_EndsTheBurstWithRetails2903()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var party = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
        var helper = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x0B000011, CharacterUid = 0x0DE0B6B3A7EB4F4CUL,
            Name = "Founder", Level = 50, MapId = 1100 };
        var member = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x0B000111, CharacterUid = 0x0DE0B6B3A7F4678CUL,
            Name = "Member", Level = 39, MapId = 1100 };
        players.AddPlayer(helper.SessionId, helper);
        players.AddPlayer(member.SessionId, member);
        var state = new PartyState { PartyId = 0x40, LeaderSessionId = helper.SessionId, FounderGid = helper.CharacterUid,
            MemberSessionIds = [helper.SessionId, member.SessionId] };
        state.Members[helper.SessionId] = helper;
        state.Members[member.SessionId] = member;
        state.FrameSlots[helper.SessionId] = 1;
        state.FrameSlots[member.SessionId] = 2;
        party.Track(state);
        state.HelperFlags[helper.CharacterUid] = HelperPackets.FlagDeclared;

        var collection = new ServiceCollection();
        collection.AddSingleton(party);
        await using var services = collection.BuildServiceProvider();
        var data = new GameDataStore();   
        var combat = new CombatService(players, new NpcStateManager(data, NullLogger<NpcStateManager>.Instance), broadcast,
            data, NullLogger<CombatService>.Instance, services.GetRequiredService<IServiceScopeFactory>());

        await combat.GrantExpAsync(member, 2 * 999_999UL);
        Assert.Equal(41, member.Level);

        var memberOps = new List<ushort>();
        string notice = "";
        while (true)
        {
            var (op, body) = await net.Readers[1].NextAsync();
            memberOps.Add(op);
            if (op == ZoneS2COpcodes.NfyHelperNotice) notice = Hex(body);
            if (op == ZoneS2COpcodes.OnPartyNotifyUpdate) break;
        }
        Assert.Equal(new[] { ZoneS2COpcodes.OnLevelUp, ZoneS2COpcodes.OnLevelUpNotify, ZoneS2COpcodes.OnGetTalentPoint,
            ZoneS2COpcodes.NfyHelperNotice, ZoneS2COpcodes.OnPartyNotifyUpdate }, memberOps.ToArray());
        Assert.Equal(Retail2903, notice);
        Assert.False(state.HelperFlags.ContainsKey(helper.CharacterUid));

        
        Assert.Equal(ZoneS2COpcodes.OnLevelUpNotify, (await net.Readers[0].NextAsync()).Op);
        Assert.Equal(ZoneS2COpcodes.OnPartyNotifyUpdate, (await net.Readers[0].NextAsync()).Op);
    }
}
