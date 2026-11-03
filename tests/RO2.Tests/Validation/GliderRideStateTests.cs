using RO2.Companions.Domain;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>The pure glider ride / flight-token state machine, without the host's per-player holder.</summary>
public class GliderRideStateTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ride_IsTakenOnce()
    {
        var s = new GliderRideState();
        Assert.False(s.HasRide);
        Assert.False(s.TryTakeRide(out _));
        s.PutRide(3);
        s.PutRide(7); // newer replaces
        Assert.True(s.HasRide);
        Assert.True(s.TryTakeRide(out var route));
        Assert.Equal((ushort)7, route);
        Assert.False(s.HasRide);
        Assert.False(s.TryTakeRide(out _));
    }

    [Fact]
    public void FlightToken_SingleUseAndLifetimeIs17Seconds()
    {
        var s = new GliderRideState();
        Assert.Equal(TimeSpan.FromSeconds(17), GliderRideState.FlightTokenLifetime);
        Assert.False(s.TryConsumeFlightToken(15, T0)); // bare 1528
        s.IssueFlightToken(15, T0 + GliderRideState.FlightTokenLifetime);
        Assert.True(s.TryConsumeFlightToken(15, T0.AddSeconds(1)));
        Assert.False(s.TryConsumeFlightToken(15, T0.AddSeconds(2)));
    }

    [Fact]
    public void FlightToken_OtherStationIsRefusedAndKeepsTheToken()
    {
        var s = new GliderRideState();
        s.IssueFlightToken(15, T0.AddSeconds(17));
        Assert.False(s.TryConsumeFlightToken(16, T0.AddSeconds(1)));
        Assert.True(s.TryConsumeFlightToken(15, T0.AddSeconds(2)));
    }

    [Fact]
    public void FlightToken_ExpiredIsDroppedAndExpiryInstantIsStillValid()
    {
        var s = new GliderRideState();
        var expiry = T0.AddSeconds(17);
        s.IssueFlightToken(15, expiry);
        Assert.False(s.TryConsumeFlightToken(15, expiry.AddTicks(1)));
        Assert.False(s.TryConsumeFlightToken(15, T0)); // dropped, not revived
        s.IssueFlightToken(15, expiry);
        Assert.True(s.TryConsumeFlightToken(15, expiry));
    }

    [Fact]
    public void FlightToken_NewerIssueSupersedesAndRideIsIndependent()
    {
        var s = new GliderRideState();
        s.PutRide(4);
        s.IssueFlightToken(15, T0.AddSeconds(17));
        s.IssueFlightToken(16, T0.AddSeconds(17));
        Assert.False(s.TryConsumeFlightToken(15, T0));
        Assert.True(s.TryConsumeFlightToken(16, T0));
        Assert.True(s.HasRide);
    }
}
