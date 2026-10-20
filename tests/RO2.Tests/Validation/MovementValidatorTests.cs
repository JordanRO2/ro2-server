using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

public class MovementValidatorTests
{
    private const uint Map = 1100;
    private static readonly MovePos Origin = new(100f, 100f, 5f);

 /// <summary>Small synthetic navmesh: one square of walkable ground on one map, nothing known elsewhere.</summary>
    private sealed class SquareSurface : IWalkableSurface
    {
        public bool? IsWalkable(uint mapId, MovePos p)
            => mapId != Map ? null : p.X >= 0 && p.X <= 500 && p.Y >= 0 && p.Y <= 500;
    }

    [Theory]
    [InlineData(float.NaN, 100f, 5f)]
    [InlineData(100f, float.PositiveInfinity, 5f)]
    [InlineData(100f, 100f, float.NegativeInfinity)]
    public void NonFiniteDestinationIsRefused(float x, float y, float z)
        => Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, Origin, new MovePos(x, y, z), 1f, 6f));

    [Fact]
    public void NonFiniteCurrentDtOrSpeedIsRefused()
    {
        var ok = new MovePos(101f, 100f, 5f);
        Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, new MovePos(float.NaN, 0, 0), ok, 1f, 6f));
        Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, Origin, ok, float.NaN, 6f));
        Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, Origin, ok, 1f, float.PositiveInfinity));
        Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, Origin, ok, -1f, 6f));
        Assert.Equal(MoveRefusal.NonFinite, MovementValidator.Validate(Map, Origin, ok, 1f, -6f));
    }

    [Fact]
    public void InSpeedStepPasses()
    {
        
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(106f, 100f, 5f), 1f, 6f));
        
        double max = MovementValidator.MaxStep(6f, 1f);
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(100f + (float)max - 0.001f, 100f, 5f), 1f, 6f));
    }

    [Fact]
    public void OverSpeedJumpIsRefused()
    {
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(1100f, 100f, 5f), 1f, 6f));
        
        double max = MovementValidator.MaxStep(6f, 1f);
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(100f + (float)max + 0.01f, 100f, 5f), 1f, 6f));
    }

    [Fact]
    public void ZeroElapsedTimeAllowsOnlyTheHalfSecondOfTravel()
    {
        
        Assert.Equal(3.0, MovementValidator.FixedTolerance(6f));
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, Origin, 0f, 6f));
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(100.5f, 100f, 5f), 0f, 6f));
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(102.9f, 100f, 5f), 0f, 6f));
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(103.1f, 100f, 5f), 0f, 6f));
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(110f, 100f, 5f), 0f, 6f));
    }

    [Fact]
    public void CapIsOneTimesSpeedPlusHalfASecond_NotOnePointFive()
    {
        
        Assert.Equal(1.0f, MovementValidator.Slack);
        Assert.Equal(63.0, MovementValidator.MaxStep(6f, 10f), 4);
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(162.9f, 100f, 5f), 10f, 6f));
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(163.1f, 100f, 5f), 10f, 6f));
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, new MovePos(190f, 100f, 5f), 10f, 6f));   
    }

    [Fact]
    public void ServerGrantedExemptionSkipsOnlyTheSpeedCheck()
    {
        var dash = new MovePos(140f, 100f, 5f);   
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, dash, 0.5f, 6f));
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, dash, 0.5f, 6f, serverGrantedExemption: true));
        
        Assert.Equal(MoveRefusal.OutOfBounds,
            MovementValidator.Validate(Map, Origin, dash, 0.5f, 6f, new MoveBounds(0, 120, 0, 200), serverGrantedExemption: true));
        Assert.Equal(MoveRefusal.NotWalkable,
            MovementValidator.Validate(Map, new MovePos(495f, 100f, 5f), new MovePos(535f, 100f, 5f), 0.5f, 6f, surface: new SquareSurface(), serverGrantedExemption: true));
    }

    [Fact]
    public void DiagonalStepUsesEuclideanLength()
    {
        
        var dest = new MovePos(130f, 140f, 5f);
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, dest, 8f, 6f));
        Assert.Equal(MoveRefusal.TooFast, MovementValidator.Validate(Map, Origin, dest, 5f, 6f));
    }

    [Fact]
    public void ZChangeAloneDoesNotCountAsSpeed()
        => Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(100f, 100f, 500f), 0.1f, 6f));

    [Fact]
    public void OffMeshDestinationIsFlagged()
    {
        var surface = new SquareSurface();
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(110f, 100f, 5f), 5f, 6f, surface: surface));
        Assert.Equal(MoveRefusal.NotWalkable, MovementValidator.Validate(Map, new MovePos(495f, 100f, 5f), new MovePos(505f, 100f, 5f), 5f, 6f, surface: surface));
    }

    [Fact]
    public void UnknownWalkabilityIsNotARefusal()
    {
        
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(9999, Origin, new MovePos(110f, 100f, 5f), 5f, 6f, surface: new SquareSurface()));
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(110f, 100f, 5f), 5f, 6f));
    }

    [Fact]
    public void OutOfBoundsDestinationIsRefusedWhenBoundsAreGiven()
    {
        var bounds = new MoveBounds(0, 200, 0, 200);
        Assert.Equal(MoveRefusal.None, MovementValidator.Validate(Map, Origin, new MovePos(110f, 100f, 5f), 5f, 6f, bounds));
        Assert.Equal(MoveRefusal.OutOfBounds, MovementValidator.Validate(Map, new MovePos(195f, 100f, 5f), new MovePos(205f, 100f, 5f), 5f, 6f, bounds));
    }
}
