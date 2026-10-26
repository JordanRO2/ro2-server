using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// Tests for the server-side movement gate. <see cref="MovementValidator"/> holds the movement rule; these tests pin
/// how <see cref="MovementGuard"/> keeps a per-character baseline, refuses over-speed, non-finite and teleport positions
/// without applying them, and exempts server-granted skill moves. The clock is injected so every time delta is
/// deterministic.
/// </summary>
public class MovementGuardTests
{
    private const uint Map = 1100;
    private const float Speed = 6f; 

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    private static PlayerState PlayerAt(float x, float y, float z = 5f)
        => new() { EntityId = 0x00100111u, MapId = Map, X = x, Y = y, Z = z };

    [Fact]
    public void TeleportIsRefusedAndStoredPositionIsUnchanged()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        p.LastValidatedMoveAtUtc = clock.UtcNow; 
        clock.Advance(1.0);                      

        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(p, new MovePos(1000f, 100f, 5f), Speed));
        Assert.Equal((100f, 100f, 5f), (p.X, p.Y, p.Z)); 
    }

    [Fact]
    public void SustainedOverSpeedMoveIsRefused()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        p.LastValidatedMoveAtUtc = clock.UtcNow;
        clock.Advance(1.0); 

        
        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(p, new MovePos(120f, 100f, 5f), Speed));
        Assert.Equal(100f, p.X);
    }

    [Fact]
    public void InSpeedMoveIsAppliedAndAdvancesTheBaseline()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        p.LastValidatedMoveAtUtc = clock.UtcNow;
        clock.Advance(1.0);

        
        Assert.Equal(MoveRefusal.None, guard.ValidateClientMove(p, new MovePos(106f, 100f, 5f), Speed));
        Assert.Equal((106f, 100f, 5f), (p.X, p.Y, p.Z));
        Assert.Equal(clock.UtcNow, p.LastValidatedMoveAtUtc); 
    }

    [Theory]
    [InlineData(float.NaN, 100f, 5f)]
    [InlineData(100f, float.PositiveInfinity, 5f)]
    [InlineData(100f, 100f, float.NegativeInfinity)]
    public void NonFinitePositionIsRefused(float x, float y, float z)
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        p.LastValidatedMoveAtUtc = clock.UtcNow;

        Assert.Equal(MoveRefusal.NonFinite, guard.ValidateClientMove(p, new MovePos(x, y, z), Speed));
        Assert.Equal((100f, 100f, 5f), (p.X, p.Y, p.Z));
    }

    [Fact]
    public void ServerGrantedSkillMoveExceedingTheCapIsAcceptedViaTheExemption()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        p.LastValidatedMoveAtUtc = clock.UtcNow;
        clock.Advance(0.5); 

        var dash = new MovePos(140f, 100f, 5f);
        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(p, dash, Speed)); 
        Assert.Equal(100f, p.X);                                                     
        Assert.Equal(MoveRefusal.None, guard.ApplyServerGranted(p, dash));           
        Assert.Equal((140f, 100f), (p.X, p.Y));
    }

    [Fact]
    public void RefusedMoveDoesNotAdvanceTheBaseline()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f);
        var t0 = clock.UtcNow;
        p.LastValidatedMoveAtUtc = t0;
        clock.Advance(1.0);

        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(p, new MovePos(1000f, 100f, 5f), Speed));
        
        
        Assert.Equal(t0, p.LastValidatedMoveAtUtc);
    }

    [Fact]
    public void CombatRangeGateSeesTheValidatedPositionNotAClaimedTeleport()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);
        var p = PlayerAt(100f, 100f); 
        p.LastValidatedMoveAtUtc = clock.UtcNow;
        clock.Advance(0.2);
        const float targetX = 160f, targetY = 100f; 
        Assert.False(AutoAttackState.InRange(p, targetX, targetY)); 

        
        
        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(p, new MovePos(159f, 100f, 5f), Speed));
        Assert.Equal((100f, 100f), (p.X, p.Y));
        Assert.False(AutoAttackState.InRange(p, targetX, targetY));
    }

    [Fact]
    public void FirstMoveSeedsTheBaselineAndStillRejectsAFirstPacketTeleport()
    {
        var clock = new TestClock();
        var guard = new MovementGuard(clock);

        
        
        var ok = PlayerAt(100f, 100f);
        Assert.Equal(MoveRefusal.None, guard.ValidateClientMove(ok, new MovePos(102f, 100f, 5f), Speed));
        Assert.Equal(clock.UtcNow, ok.LastValidatedMoveAtUtc);

        var teleporter = PlayerAt(100f, 100f);
        Assert.Equal(MoveRefusal.TooFast, guard.ValidateClientMove(teleporter, new MovePos(500f, 100f, 5f), Speed));
        Assert.Equal(100f, teleporter.X);
    }
}
