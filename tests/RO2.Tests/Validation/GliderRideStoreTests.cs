using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// The per-player glider identity store (MV-2/MV-3/MV-6): the 1313 ride record is consumed once, the
/// 1515 flight token is consumed once, only for its own station and only inside its expiry.
/// </summary>
public class GliderRideStoreTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ride_IsTakenOnceSoARepeated1314CannotDoubleCharge()
    {
        var p = new PlayerState();
        Assert.False(GliderRideStore.HasRide(p));
        Assert.False(GliderRideStore.TryTakeRide(p, out _)); // 1314 without a 1313

        GliderRideStore.PutRide(p, 7);
        Assert.True(GliderRideStore.HasRide(p)); // the "already mounted" signal
        Assert.True(GliderRideStore.TryTakeRide(p, out var route));
        Assert.Equal((ushort)7, route);
        Assert.False(GliderRideStore.HasRide(p));
        Assert.False(GliderRideStore.TryTakeRide(p, out _)); // replay
    }

    [Fact]
    public void Ride_IsPerPlayer()
    {
        var a = new PlayerState();
        var b = new PlayerState();
        GliderRideStore.PutRide(a, 3);
        Assert.False(GliderRideStore.TryTakeRide(b, out _));
        Assert.True(GliderRideStore.TryTakeRide(a, out _));
    }

    [Fact]
    public void FlightToken_ConsumesOnceForItsStation()
    {
        var p = new PlayerState();
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 15, T0)); // bare 1528

        GliderRideStore.IssueFlightToken(p, 15, T0.AddSeconds(17));
        Assert.True(GliderRideStore.TryConsumeFlightToken(p, 15, T0.AddSeconds(1)));
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 15, T0.AddSeconds(2))); // replay after consume
    }

    [Fact]
    public void FlightToken_OtherStationIsRefusedAndKeepsTheToken()
    {
        var p = new PlayerState();
        GliderRideStore.IssueFlightToken(p, 15, T0.AddSeconds(17));
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 16, T0.AddSeconds(1))); // 1515 A then 1528 B
        Assert.True(GliderRideStore.TryConsumeFlightToken(p, 15, T0.AddSeconds(2)));  // the real flight still goes
    }

    [Fact]
    public void FlightToken_ExpiresAfterItsWindowAndIsDropped()
    {
        var p = new PlayerState();
        var expiry = T0.AddSeconds(17);
        GliderRideStore.IssueFlightToken(p, 15, expiry);
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 15, expiry.AddTicks(1)));
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 15, T0)); // expired token is gone, not revived

        GliderRideStore.IssueFlightToken(p, 15, expiry);
        Assert.True(GliderRideStore.TryConsumeFlightToken(p, 15, expiry)); // exactly at expiry is still valid
    }

    [Fact]
    public void FlightToken_NewerIssueReplacesTheOlder()
    {
        var p = new PlayerState();
        GliderRideStore.IssueFlightToken(p, 15, T0.AddSeconds(17));
        GliderRideStore.IssueFlightToken(p, 16, T0.AddSeconds(17));
        Assert.False(GliderRideStore.TryConsumeFlightToken(p, 15, T0));
        Assert.True(GliderRideStore.TryConsumeFlightToken(p, 16, T0));
    }

    [Fact]
    public void RideAndToken_AreIndependent()
    {
        var p = new PlayerState();
        GliderRideStore.PutRide(p, 4);
        GliderRideStore.IssueFlightToken(p, 15, T0.AddSeconds(17));
        Assert.True(GliderRideStore.TryConsumeFlightToken(p, 15, T0));
        Assert.True(GliderRideStore.HasRide(p));
    }
}
