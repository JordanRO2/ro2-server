using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The duel-state 2398 pairs (FieldPvPStatePackets) and the started flag (FieldPvPService.Duel.Started),
/// including a session with several consecutive duels between the same two players.
/// </summary>
public class FieldPvPStatePacketsTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    [Fact]
    public void StartPair_ReproducesTheArchiveFramesBeforeThe2758()
    {
        
        
        Assert.Equal("011b1b00eae2114def06114def060000013d050000ea03000000000000",
            Hex(FieldPvPStatePackets.BuildStart2398(0x06EF4D11, 0x53D, FieldPvPStartPackets.ChallengerSide)));
        Assert.Equal("011b1b00eae2115def06115def060000013d050000eb03000000000000",
            Hex(FieldPvPStatePackets.BuildStart2398(0x06EF5D11, 0x53D, FieldPvPStartPackets.TargetSide)));
        Assert.Equal("011010005ee4114def06ea030000eb030000", Hex(FieldPvPStartPackets.BuildStart2758(0x06EF4D11)));
    }

    [Fact]
    public void StartPair_CarriesTheSideOfTheRole_NotOfThePlayer()
    {
        
        
        Assert.Equal("011b1b00eae2114def06114def060000013a050000eb03000000000000",
            Hex(FieldPvPStatePackets.BuildStart2398(0x06EF4D11, 0x53A, FieldPvPStartPackets.TargetSide)));
        Assert.Equal("011b1b00eae2115def06115def060000013a050000ea03000000000000",
            Hex(FieldPvPStatePackets.BuildStart2398(0x06EF5D11, 0x53A, FieldPvPStartPackets.ChallengerSide)));
    }

    [Fact]
    public void EndPair_ReproducesTheDumpFramesAfterTheResult()
    {
        
        
        Assert.Equal("011b1b00eae2114def06114def06000000000000000200000000000000", Hex(FieldPvPStatePackets.BuildEnd2398(0x06EF4D11)));
        Assert.Equal("011b1b00eae2115def06115def06000000000000000200000000000000", Hex(FieldPvPStatePackets.BuildEnd2398(0x06EF5D11)));
        Assert.Equal(CombatPackets.BuildChangeState2398(0x06EF5D11, CombatPackets.ActorState.PcNormal, trailing: 2),
            FieldPvPStatePackets.BuildEnd2398(0x06EF5D11));
    }

    [Fact]
    public void Started_IsSetOnlyForAHeldAcceptedDuel()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        PlayerState Pc(uint s)
        {
            var p = new PlayerState { SessionId = s, EntityId = PlayerStateManager.BuildActorHandle(s), MapId = 1100, Hp = 100 };
            players.AddPlayer(s, p);
            return p;
        }
        var a = Pc(0xA53001); var b = Pc(0xA53002);
        var duel = FieldPvPService.Open(a, b, players);
        Assert.NotNull(duel);
        try
        {
            
            Assert.False(FieldPvPService.TryMarkStarted(duel));
            Assert.Null(FieldPvPService.StartedDuelOf(a.EntityId, players));

            
            Assert.True(FieldPvPService.TryAccept(duel));
            Assert.Null(FieldPvPService.StartedDuelOf(a.EntityId, players));

            Assert.True(FieldPvPService.TryMarkStarted(duel));
            Assert.True(duel.Started);
            Assert.Same(duel, FieldPvPService.StartedDuelOf(a.EntityId, players));
            Assert.Same(duel, FieldPvPService.StartedDuelOf(b.EntityId, players));
        }
        finally
        {
            FieldPvPService.TryClose(duel.Id, out _);
        }

        
        Assert.Null(FieldPvPService.StartedDuelOf(a.EntityId, players));
        Assert.False(FieldPvPService.TryMarkStarted(duel));
    }
}
