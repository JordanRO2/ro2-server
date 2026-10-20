using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2758 NfyStartFiledPvP, byte for byte against retail, and the rule that only a duel still held and
/// accepted gets one.
/// </summary>
public class FieldPvPStart2758Tests
{
    [Fact]
    public void Start_2758_MatchesRetail()
    {
        
        
        Assert.Equal("011010005ee4113d780bea030000eb030000",
            Convert.ToHexString(FieldPvPStartPackets.BuildStart2758(0x0B783D11)).ToLowerInvariant());
    }

    [Fact]
    public void Start_OnlyForTheSameDuelStillAccepted()
    {
        var accepted = new FieldPvPService.Duel { Id = 7, State = FieldPvPService.DuelState.Accepted };
        Assert.True(FieldPvPStartPackets.StillStarts(accepted, accepted));
        Assert.False(FieldPvPStartPackets.StillStarts(accepted, null)); 
        var other = new FieldPvPService.Duel { Id = 7, State = FieldPvPService.DuelState.Accepted };
        Assert.False(FieldPvPStartPackets.StillStarts(accepted, other));
        var pending = new FieldPvPService.Duel { Id = 8, State = FieldPvPService.DuelState.Pending };
        Assert.False(FieldPvPStartPackets.StillStarts(pending, pending));
    }
}

/// <summary>
/// The party Helper flag: 2903 checked byte for byte against retail, the 11-byte 2902, and the flag byte at roster
/// record offset 63.
/// </summary>
public class HelperFlagTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    [Fact]
    public void Notice_2903_MatchesRetail()
    {
        
        
        Assert.Equal("4c4feba7b3b6e00d00",
            Convert.ToHexString(HelperPackets.BuildHelperNotice2903(0x0DE0B6B3A7EB4F4CUL, HelperPackets.FlagQuit))
                .ToLowerInvariant());
    }

    [Fact]
    public void Answer_2902_IsGidFlagResult()
    {
        
        Assert.Equal("4c4feba7b3b6e00d010000",
            Convert.ToHexString(HelperPackets.BuildAnsHelperDeclare2902(0x0DE0B6B3A7EB4F4CUL, HelperPackets.FlagDeclared, 0))
                .ToLowerInvariant());
    }

    [Fact]
    public async Task Declare_SetsRec63_ForAnEligibleDeclarerOnly_AndLeavingClearsIt()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);
        var founder = new PlayerState { SessionId = 3, CharacterUid = 0x0DE0B6B3A7EB4F4CUL, Name = "PSFarmA11", Level = 40 };
        var other = new PlayerState { SessionId = 5, CharacterUid = 0x0DE0B6B3A7F4678CUL, Name = "Other", Level = 30 };
        players.AddPlayer(3, founder);
        players.AddPlayer(5, other);
        var party = new PartyState { PartyId = 0x40, LeaderSessionId = 3, FounderGid = founder.CharacterUid, MemberSessionIds = [3, 5] };
        party.Members[3] = founder;
        party.Members[5] = other;
        party.FrameSlots[3] = 1;
        party.FrameSlots[5] = 2;
        service.Track(party);

        Assert.Equal(HelperDeclareResult.Done, await service.SetHelperFlagAsync(founder, HelperPackets.FlagDeclared));
        var recs = Records(service.BuildRosterBody(party));
        Assert.Equal(1, recs[0][HelperPackets.RecHelperFlagOff]);
        Assert.Equal(0, recs[1][HelperPackets.RecHelperFlagOff]);

        Assert.Equal(HelperDeclareResult.Done, await service.SetHelperFlagAsync(founder, HelperPackets.FlagQuit));
        Assert.Equal(0, Records(service.BuildRosterBody(party))[0][HelperPackets.RecHelperFlagOff]);

        
        
        Assert.Equal(HelperDeclareResult.NotEligible, await service.SetHelperFlagAsync(other, HelperPackets.FlagDeclared));
        Assert.False(party.HelperFlags.ContainsKey(other.CharacterUid));

        
        Assert.Equal(HelperDeclareResult.Done, await service.SetHelperFlagAsync(founder, HelperPackets.FlagDeclared));
        await service.LeavePartyAsync(3, PartyLeaveReason.Left);
        Assert.False(party.HelperFlags.ContainsKey(founder.CharacterUid));

        
        var loner = new PlayerState { SessionId = 9, CharacterUid = 0x0DE0B6B3A7000009UL, Name = "Loner" };
        players.AddPlayer(9, loner);
        Assert.Equal(HelperDeclareResult.NotInParty, await service.SetHelperFlagAsync(loner, HelperPackets.FlagDeclared));
    }

 /// <summary>The 208-byte records of a 2361 body ([02 lo hi][selfLen][msgTag] + 63-byte header).</summary>
    private static byte[][] Records(byte[] body)
    {
        var recs = body.AsSpan(7 + 63);
        var list = new byte[recs.Length / 208][];
        for (var i = 0; i < list.Length; i++) list[i] = recs.Slice(i * 208, 208).ToArray();
        return list;
    }
}

/// <summary>Which holder a 1302 logout removes the online-account entry from.</summary>
public class LogoutOnlineEntryTests
{
    [Fact]
    public void Logout_ReleasesItsOwnEntry_AndAClosedPredecessors()
    {
        var own = OnlineAccountRules.OwnerFor(12);
        Assert.Equal([own], ZoneAuthHandler.LogoutReleasableOwners(own, 12, _ => true));

        
        var predecessor = OnlineAccountRules.OwnerFor(8);
        Assert.Equal([own, predecessor], ZoneAuthHandler.LogoutReleasableOwners(predecessor, 12, id => id != 8));

        
        Assert.Equal([own], ZoneAuthHandler.LogoutReleasableOwners(predecessor, 12, _ => true));
        Assert.Equal([own], ZoneAuthHandler.LogoutReleasableOwners("other-process:8", 12, _ => false));
    }

    [Fact]
    public void Owner_ParsesOnlyThisProcesssSessionIds()
    {
        Assert.True(OnlineAccountRules.TryGetLocalSessionId(OnlineAccountRules.OwnerFor(42), out var id));
        Assert.Equal(42u, id);
        Assert.False(OnlineAccountRules.TryGetLocalSessionId("x:42", out _));
        Assert.False(OnlineAccountRules.TryGetLocalSessionId(OnlineAccountRules.ProcessInstance + ":-1", out _));
    }
}
