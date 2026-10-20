using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The 20-unit duel range (FieldPvPService.DuelRange): refusal code 21 for 1517 and accept refusal 18 for 1518,
/// with positions taken from each player's last 1526/2750 report, plus 40.
/// </summary>
public class FieldPvPRangeTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static PlayerState Pc(PlayerStateManager players, uint session, float x, float y)
    {
        var p = new PlayerState { SessionId = session, EntityId = PlayerStateManager.BuildActorHandle(session), MapId = 1100, X = x, Y = y, Hp = 100 };
        players.AddPlayer(session, p);
        return p;
    }

    [Fact]
    public void Challenge_OutOfRange_IsRefusedWith21_AsRetailDid()
    {
        
        
        var request = Convert.FromHexString("010c0c0053e400000000111a5b0a");
        Assert.Equal(0x0A5B1A11u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(FieldPvPHandler.TargetOffset)));

        var players = new PlayerStateManager(new NoDirtySink());
        var challenger = Pc(players, 0xA52001, 741.5338f, 659.0634f);
        var target = Pc(players, 0xA52002, 759.6472f, 650.5181f);
        Assert.False(FieldPvPService.WithinDuelRange(challenger, target));
        Assert.Equal(FieldPvPRejectPackets.TooFar, FieldPvPHandler.RefusalCode(challenger, target, players));

        Assert.Equal("010a0a005de411a15b0a1500", Hex(FieldPvPRejectPackets.BuildReject2742(0x0A5BA111, FieldPvPRejectPackets.TooFar)));
    }

    [Fact]
    public void Challenge_JustInsideRange_GoesOut()
    {
        
        
        
        var players = new PlayerStateManager(new NoDirtySink());
        var challenger = Pc(players, 0xA52003, 746.35498f, 743.56195f);
        var target = Pc(players, 0xA52004, 726.48206f, 742.31628f);
        Assert.True(FieldPvPService.WithinDuelRange(challenger, target));
        Assert.Null(FieldPvPHandler.RefusalCode(challenger, target, players));
        Assert.Equal("0114140057e411232a0b11232a0b110b2c0b73040000",
            Hex(FieldPvPPackets.BuildCertify2735(0x0B2A2311, 0x0B2A2311, 0x0B2C0B11, 0x473)));
    }

    [Fact]
    public void BusyTarget_KeepsItsCodeWhateverTheDistance()
    {
        
        
        var players = new PlayerStateManager(new NoDirtySink());
        var a = Pc(players, 0xA52005, 0f, 0f);
        var b = Pc(players, 0xA52006, 100f, 0f);
        var c = Pc(players, 0xA52007, 100f, 5f);
        var duel = FieldPvPService.Open(b, c, players);
        Assert.NotNull(duel);
        try
        {
            Assert.Equal(FieldPvPRejectPackets.TargetOnBattleCue, FieldPvPHandler.RefusalCode(a, b, players));
        }
        finally
        {
            FieldPvPService.TryClose(duel.Id, out _);
        }
    }

    [Theory]
    [InlineData(20f, 0f, true)]
    [InlineData(20.03f, 0f, false)]
    [InlineData(12f, 16f, true)]      
    [InlineData(12f, 16.01f, false)]
    public void DuelRange_Is20UnitsIn2D(float dx, float dy, bool within)
    {
        var a = new PlayerState { X = 500f, Y = 500f, Z = 0f };
        var b = new PlayerState { X = 500f + dx, Y = 500f + dy, Z = 400f };   
        Assert.Equal(within, FieldPvPService.WithinDuelRange(a, b));
    }

    [Fact]
    public void Accept_OutOfRange_Answers18ThenTheEndPair()
    {
        
        
        
        Assert.Equal("0122220058e411a15b0a5e04000011a15b0a111a5b0a0000000000000000000000001200",
            Hex(FieldPvPPackets.BuildAccept2737(0x0A5BA111, 0x45E, 0x0A5BA111, 0x0A5B1A11, 0f, 0f, 0f,
                FieldPvPPackets.AcceptTooFar)));
        Assert.Equal("011b1b00eae211a15b0a11a15b0a000000000000000200000000000000",
            Hex(FieldPvPStatePackets.BuildEnd2398(0x0A5BA111)));
        Assert.Equal("011b1b00eae2111a5b0a111a5b0a000000000000000200000000000000",
            Hex(FieldPvPStatePackets.BuildEnd2398(0x0A5B1A11)));
    }
}
