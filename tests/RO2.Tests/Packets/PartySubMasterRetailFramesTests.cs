using System.Buffers.Binary;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1503 ReqPartySubMaster and 1505 ReqPartyDismissSubMaster with their answers 2702/2703 and 2706/2707, and the
/// sub-master slots that every roster header carries at payload+30, +38 and +46.
/// </summary>
public class PartySubMasterRetailFramesTests
{
    private const ulong LeaderGid = 0x0DE0B6B3A7E6F919UL;   
    private const ulong FirstGid = 0x0DE0B6B3A7C45ED7UL;    
    private const ulong SecondGid = 0x0DE0B6B3A7E84ACCUL;   

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Answers_MatchRetail()
    {
        Assert.Equal("0000", Hex(PartyStatePackets.BuildAnsPartySubMaster2702()));
        Assert.Equal("0110100041e400000000d75ec4a7b3b6e00d", Hex(PartyStatePackets.BuildNfyPartySubMaster(FirstGid)));
        Assert.Equal("0110100041e400000000cc4ae8a7b3b6e00d", Hex(PartyStatePackets.BuildNfyPartySubMaster(SecondGid)));
        
        Assert.Equal("0000", Hex(PartyStatePackets.BuildAnsPartyDismissSubMaster2706()));
        Assert.Equal("0110100043e40000000019f9e6a7b3b6e00d", Hex(PartyStatePackets.BuildNfyPartyDismissSubmaster(LeaderGid)));
    }

    private static (PartyService Service, PartyState Party) NewRaid()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);
        
        
        ulong[] gids = [LeaderGid, FirstGid, SecondGid, 0x0DE0B6B3A7000001UL, 0x0DE0B6B3A7000002UL, 0x0DE0B6B3A7000003UL];
        var party = new PartyState
        {
            PartyId = 0x20, LeaderSessionId = 7, PartyType = 2, ItemRule = 2, ItemQuality = 4, FounderGid = LeaderGid,
            MemberSessionIds = [7, 8, 9, 10, 11, 12],
        };
        for (var i = 0; i < gids.Length; i++)
        {
            var key = (uint)(7 + i);
            var p = new PlayerState { SessionId = key, CharacterUid = gids[i], Name = $"M{i}", Level = 50 };
            if (i == 0) players.AddPlayer(key, p);
            party.Members[key] = p;
            party.FrameSlots[key] = (byte)(i + 1);
        }
        service.Track(party);
        return (service, party);
    }

    
    
    private const string RosterBefore = 
        "022305230535e300000000152000000200020619f9e6a7b3b6e00d0000000000000000040000000000000000000000000000000000000000000000000019f9e6a7b3b6e00d06";
    private const string RosterAfterTwo = 
        "022305230535e300000000152000000200020619f9e6a7b3b6e00d00000000000000000400d75ec4a7b3b6e00dcc4ae8a7b3b6e00d000000000000000019f9e6a7b3b6e00d06";
    private const string RosterAfterDismiss = 
        "022305230535e300000000152000000200020619f9e6a7b3b6e00d00000000000000000400d75ec4a7b3b6e00d0000000000000000000000000000000019f9e6a7b3b6e00d06";

    [Fact]
    public async Task Roster_CarriesTheSlots_AsRetailDid_ThroughTwoPromotionsAndADismissal()
    {
        var (service, party) = NewRaid();
        Assert.Equal(RosterBefore, Hex(service.BuildRosterBody(party)[..70]));

        Assert.True(await service.SetSubMasterAsync(7, FirstGid));
        Assert.True(await service.SetSubMasterAsync(7, SecondGid));
        var body = service.BuildRosterBody(party);
        Assert.Equal(1318, body.Length);
        Assert.Equal(RosterAfterTwo, Hex(body[..70]));

        Assert.True(await service.DismissSubMasterAsync(7, SecondGid));
        Assert.Equal(RosterAfterDismiss, Hex(service.BuildRosterBody(party)[..70]));
    }

 /// A leader promotes a member to sub-master and then hands it the lead. The next roster names the new leader and
 /// keeps it in the first sub-master slot: a leader change leaves the slots alone.
    [Fact]
    public async Task LeaderChange_KeepsTheSubMasterSlot_AsRetailDid()
    {
        const ulong founder = 0x37A661C10D637B7EUL;   
        const ulong promoted = 0x0DE0B6B3A799CDD7UL;  
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);
        var party = new PartyState { PartyId = 0x338, LeaderSessionId = 20, PartyType = 2, ItemRule = 2, ItemQuality = 2, FounderGid = founder };
        for (var i = 0; i < 13; i++)
        {
            var key = (uint)(20 + i);
            var gid = i switch { 0 => founder, 1 => promoted, _ => 0x0DE0B6B3A7100000UL + (ulong)i };
            var p = new PlayerState { SessionId = key, CharacterUid = gid, Name = $"R{i}", Level = 50 };
            players.AddPlayer(key, p);   
            party.Members[key] = p;
            party.FrameSlots[key] = (byte)(i + 1);
            party.MemberSessionIds.Add(key);
        }
        service.Track(party);
        Assert.Equal(
            "02d30ad30a35e300000000153803000200020d7e7b630dc161a637000000000000000002000000000000000000000000000000000000000000000000007e7b630dc161a6370d",
            Hex(service.BuildRosterBody(party)[..70]));   

        Assert.True(await service.SetSubMasterAsync(20, promoted));
        await service.ChangeLeaderAsync(20, PlayerStateManager.BuildActorHandle(20), promoted);
        Assert.Equal(21u, party.LeaderSessionId);
        Assert.Equal(
            "02d30ad30a35e300000000153803000200020dd7cd99a7b3b6e00d00000000000000000200d7cd99a7b3b6e00d000000000000000000000000000000007e7b630dc161a6370d",
            Hex(service.BuildRosterBody(party)[..70]));   
    }

    [Fact]
    public async Task Promotion_IsRefused_ForNonLeaders_NonMembers_TheLeader_Repeats_AndAFourth()
    {
        var (service, party) = NewRaid();
        Assert.False(await service.SetSubMasterAsync(8, SecondGid));                   
        Assert.False(await service.SetSubMasterAsync(7, 0x0DE0B6B3A7FFFFFFUL));        
        Assert.False(await service.SetSubMasterAsync(7, LeaderGid));                   
        Assert.False(await service.SetSubMasterAsync(7, 0));
        Assert.True(await service.SetSubMasterAsync(7, FirstGid));
        Assert.False(await service.SetSubMasterAsync(7, FirstGid));                    
        Assert.True(await service.SetSubMasterAsync(7, SecondGid));
        Assert.True(await service.SetSubMasterAsync(7, 0x0DE0B6B3A7000001UL));
        Assert.False(await service.SetSubMasterAsync(7, 0x0DE0B6B3A7000002UL));        
        Assert.Equal([FirstGid, SecondGid, 0x0DE0B6B3A7000001UL], service.SubMastersInRoster(party));
    }

 /// RemoveSubMaster clears the matching slot in place and AddSubMaster fills the first empty one. The server keeps
 /// the same slots, so its next roster agrees with the client's array.
    [Fact]
    public async Task Dismiss_FreesTheSlotInPlace_AndALeaverLosesItsSlot()
    {
        var (service, party) = NewRaid();
        await service.SetSubMasterAsync(7, FirstGid);
        await service.SetSubMasterAsync(7, SecondGid);

        Assert.False(await service.DismissSubMasterAsync(8, FirstGid));                  
        Assert.False(await service.DismissSubMasterAsync(7, 0x0DE0B6B3A7000001UL));      
        Assert.True(await service.DismissSubMasterAsync(7, FirstGid));
        Assert.Equal([0UL, SecondGid, 0UL], service.SubMastersInRoster(party));
        Assert.False(await service.DismissSubMasterAsync(7, FirstGid));                  

        Assert.True(await service.SetSubMasterAsync(7, 0x0DE0B6B3A7000002UL));          
        Assert.Equal([0x0DE0B6B3A7000002UL, SecondGid, 0UL], service.SubMastersInRoster(party));

        await service.LeavePartyAsync(9, PartyLeaveReason.Left);                         
        Assert.Equal([0x0DE0B6B3A7000002UL, 0UL, 0UL], service.SubMastersInRoster(party));
        var body = service.BuildRosterBody(party);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(7 + 38)));
    }
}
