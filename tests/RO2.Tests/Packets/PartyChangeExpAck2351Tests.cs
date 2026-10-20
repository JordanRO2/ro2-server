using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1386 ReqPartyChangeExpRule and its answer 2351 OnPartyChangeExp. Neither opcode has a reference frame, so the
/// request layout follows the client builder (a u16 9 and a u16 handle tag, the session handle at blob+4, and the
/// rule byte at blob+8). The answer is a 10-byte result blob, of which the client reads blob+8.
/// Not verified against the reference server.
/// </summary>
public class PartyChangeExpAck2351Tests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    [Fact]
    public void Ack_2351_IsTheSiblingResultBlob_WithTag58149()
    {
        var body = PartyStatePackets.BuildOnPartyChangeExpAck(0x0B783D11);
        
        Assert.Equal("010a0a0025e3113d780b0000", Convert.ToHexString(body).ToLowerInvariant());
        
        var sibling = PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeItem, 0x0B783D11);
        Assert.Equal(sibling.Length, body.Length);
        for (var i = 0; i < body.Length; i++)
            if (i is not 4 and not 5) Assert.Equal(sibling[i], body[i]);
    }

    [Fact]
    public void Request_1386_ReadsHandleAtBlob4_AndRuleAtBlob8()
    {
        
        var body = Convert.FromHexString("01090900" + "24e3" + "113d780b" + "01");
        Assert.True(PartyChangeExpRuleHandler.TryParse(body, out var handle, out var rule));
        Assert.Equal(0x0B783D11u, handle);
        Assert.Equal(1, rule);

        
        Assert.NotEqual(body[8], rule);

        
        Assert.False(PartyChangeExpRuleHandler.TryParse(Convert.FromHexString("01090900" + "22e3" + "113d780b01"), out _, out _));
        Assert.False(PartyChangeExpRuleHandler.TryParse(Convert.FromHexString("01090800" + "24e3" + "113d780b01"), out _, out _));
        Assert.False(PartyChangeExpRuleHandler.TryParse(Convert.FromHexString("0900" + "24e3" + "113d780b01"), out _, out _));
    }

    [Fact]
    public async Task ChangeExpRule_OnlyTheLeaderSetsIt()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var service = new PartyService(players,
            new BroadcastService(players, Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PartyService>.Instance);
        var party = new PartyState { PartyId = 0x20, LeaderSessionId = 7, MemberSessionIds = [7, 9] };
        service.Track(party);

        Assert.False(await service.ChangeExpRuleAsync(9, 0x911u, 1));
        Assert.Equal(0, party.ExpRule);
        Assert.True(await service.ChangeExpRuleAsync(7, 0x711u, 1));
        Assert.Equal(1, party.ExpRule);
        Assert.False(await service.ChangeExpRuleAsync(30, 0x1E11u, 0));   
        Assert.Equal(1, party.ExpRule);
    }
}
