using RO2.Domain.Entities;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2838 OnVehicleListUpdate: the periodic notice for a timed vehicle in its last hour (state 8) or just expired
/// (state 4), built by VehiclePackets.BuildVehicleListUpdate2838 and selected by VehicleExpiryNotifier.
/// </summary>
/// <remarks>
/// Reference frames are 21 bytes with one vehicle entry. The server clock is UTC-7, the clock on which the stored
/// expiries are written, and the instants in the tests are taken on that clock. Each session carries the stored
/// expiry echoed on a whole second.
/// </remarks>
public class VehicleListUpdate2838Tests
{
    
    
    
    
    private const uint Vehicle6906 = 90220000;
    private const string Soon6906 = "110000000100e0a560051cddf31f0b9ae640010800";
    private const string Expired6906 = "110000000100e0a560051cddf31f0b9ae640010400";
    
    
    private const uint VehicleF560 = 90150000;
    private const string SoonF560 = "11000000010070945f051cddf39f6699e640010800";

    
    private const uint Vehicle311f = 90140000;
    private const double Expiry311f = 46291.347650462965;
    private const string Soon311f = "110000000100606d5f051cddf31f6b9ae640010800";
    private const string Expired311f = "110000000100606d5f051cddf31f6b9ae640010400";

    [Theory]
    
    [InlineData("110000000100409061051cddf3df519ae640010800", 90280000u, 46290.558587962965, (byte)8)]
    
    [InlineData("110000000100e0a560051cddf31f0b9ae640010400", 90220000u, 46288.347650462965, (byte)4)]
    
    [InlineData(Expired311f, Vehicle311f, Expiry311f, (byte)4)]
    public void Build_ReproducesRetailsFrame(string retail, uint ridingId, double expiry, byte state)
    {
        var body = VehiclePackets.BuildVehicleListUpdate2838(new[] { new VehicleListRecord(ridingId, expiry, state) });
        Assert.Equal(retail, Convert.ToHexStringLower(body));
    }

    [Fact]
    public void Tick_GivesRetailsStateAtEachFrame_ThenStops()
    {
        var owned = new[] { new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f } };
        var login = Server(2026, 9, 26, 7, 0, 0, 0); 

        
        AssertTick(Soon311f, owned, login, Server(2026, 9, 26, 7, 35, 47, 649));
        
        var lastSoon = Server(2026, 9, 26, 8, 19, 48, 834);
        AssertTick(Soon311f, owned, login, lastSoon);
        
        var expiredTick = Server(2026, 9, 26, 8, 20, 47, 895);
        AssertTick(Expired311f, owned, lastSoon, expiredTick);
        
        Assert.Empty(VehicleExpiryNotifier.Due(owned, expiredTick, expiredTick + 60.0 / 86400));
        Assert.Empty(VehicleExpiryNotifier.Due(owned, expiredTick, Server(2026, 9, 26, 9, 41, 17, 454)));
    }

    [Fact]
    public void Tick_StartsAtTheHour()
    {
        
        
        var owned = new[] { new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f } };
        var login = Server(2026, 9, 26, 6, 0, 0, 0);

        Assert.Empty(VehicleExpiryNotifier.Due(owned, login, Server(2026, 9, 26, 7, 12, 55, 42)));
        AssertTick(Soon311f, owned, login, Server(2026, 9, 26, 7, 20, 58, 986));
    }

    [Fact]
    public void Tick_SkipsPermanentAndAlreadyAnnouncedVehicles_AndKeepsIdOrder()
    {
        var now = Server(2026, 9, 26, 8, 20, 47, 895);
        var since = now - 60.0 / 86400;
        var owned = new[]
        {
            new OwnedVehicle { RidingId = 90280000, ExpiryOle = now + 30.0 / 1440 },   
            new OwnedVehicle { RidingId = 90190000, ExpiryOle = 0 },                   
            new OwnedVehicle { RidingId = 90160000, ExpiryOle = since - 1.0 / 1440 },  
            new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f },       
        };

        var due = VehicleExpiryNotifier.Due(owned, since, now);

        Assert.Equal(new[] { (Vehicle311f, VehiclePackets.StateExpired), (90280000u, VehiclePackets.StateExpiringSoon) },
            due.Select(r => (r.RidingId, r.State)).ToArray());
        Assert.Equal(Expiry311f, due[0].ExpiryOle);
    }

    [Fact]
    public void AnExtensionPastTheHour_StopsTheTick()
    {
        
        
        var now = Expiry311f - 30.0 / 1440;
        var owned = new[] { new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f } };
        Assert.Single(VehicleExpiryNotifier.Due(owned, now - 60.0 / 86400, now));

        var extended = VehicleRules.Grant(owned, Vehicle311f, 7, now);

        Assert.Empty(VehicleExpiryNotifier.Due(extended, now, now + 60.0 / 86400));
    }

    [Fact]
    public void AGrantedExpiry_GoesOutCutToTheWholeSecond_AsRetailsLaterSessionSendsIt()
    {
        
        
        var granted = VehicleRules.Grant(Array.Empty<OwnedVehicle>(), Vehicle6906, 7, Server(2026, 9, 16, 8, 21, 48, 666));
        Assert.Equal("000000200b9ae640", Hex(granted.Single().ExpiryOle)); 

        
        
        var lastSoon = Server(2026, 9, 23, 8, 18, 46, 903);
        AssertTick(Soon6906, granted, Server(2026, 9, 23, 8, 17, 46, 275), lastSoon);
        AssertTick(Expired6906, granted, lastSoon, Server(2026, 9, 23, 8, 20, 47, 41));

        
        var grantedF560 = VehicleRules.Grant(Array.Empty<OwnedVehicle>(), VehicleF560, 7, Server(2026, 9, 11, 4, 58, 36, 842));
        Assert.Equal("000000a06699e640", Hex(grantedF560.Single().ExpiryOle));
        AssertTick(SoonF560, grantedF560, Server(2026, 9, 18, 4, 49, 15, 236), Server(2026, 9, 18, 4, 50, 15, 141));
    }

    [Theory]
    
    [InlineData("1cddf31f0b9ae640", "1cddf31f0b9ae640")]
    [InlineData("1cddf39f6699e640", "1cddf39f6699e640")]
    [InlineData("1cddf3df519ae640", "1cddf3df519ae640")]
    [InlineData("1cddf31f6b9ae640", "1cddf31f6b9ae640")]
    
    [InlineData("000000200b9ae640", "1cddf31f0b9ae640")]
    [InlineData("000000a06699e640", "1cddf39f6699e640")]
    
    [InlineData("00000080699ae640", "00000080699ae640")]
    [InlineData("0000000000000000", "0000000000000000")]
    public void WholeSecondExpiry_CutsOnlyTheFraction(string stored, string sent)
        => Assert.Equal(sent, Hex(VehiclePackets.WholeSecondExpiry(BitConverter.ToDouble(Convert.FromHexString(stored)))));

    [Fact]
    public void ClientList_WithholdsAVehicleTheLast2522LeftOut()
    {
        const uint session = 0xA2838001;
        
        Assert.Null(VehicleClientList.Holds(session, Vehicle311f));
        Assert.True(VehicleExpiryNotifier.MayName(session, Vehicle311f));

        
        VehicleClientList.Record(session, new[] { new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f } });
        Assert.True(VehicleExpiryNotifier.MayName(session, Vehicle311f));
        Assert.False(VehicleExpiryNotifier.MayName(session, 90280000)); 

        
        
        VehicleClientList.Record(session, Array.Empty<OwnedVehicle>());
        Assert.False(VehicleClientList.Holds(session, Vehicle311f));
        Assert.False(VehicleExpiryNotifier.MayName(session, Vehicle311f));
    }

    [Fact]
    public void ClientList_PrunesOfflineSessions_ButKeepsARecordNewerThanTheListing()
    {
        const uint gone = 0xA2838002, online = 0xA2838003, lateLogin = 0xA2838004;
        var vehicles = new[] { new OwnedVehicle { RidingId = Vehicle311f, ExpiryOle = Expiry311f } };
        VehicleClientList.Record(gone, vehicles);
        VehicleClientList.Record(online, vehicles);

        var listedAt = VehicleClientList.Version;        
        var players = new[] { new PlayerState { SessionId = online } };
        VehicleClientList.Record(lateLogin, vehicles);   

        VehicleClientList.Retain(players, listedAt);

        Assert.Null(VehicleClientList.Holds(gone, Vehicle311f));
        Assert.True(VehicleClientList.Holds(online, Vehicle311f));
        Assert.True(VehicleClientList.Holds(lateLogin, Vehicle311f));

        
        VehicleClientList.Retain(players, VehicleClientList.Version);
        Assert.Null(VehicleClientList.Holds(lateLogin, Vehicle311f));
        Assert.True(VehicleClientList.Holds(online, Vehicle311f));
    }

    [Fact]
    public void Watch_StartsAtLogin_AdvancesOnlyWhenMarked_AndForgetsOfflineSessions()
    {
        var watch = new VehicleExpiryWatch();
        var player = new PlayerState { SessionId = 7, LoginTime = new DateTime(2026, 9, 26, 14, 0, 0, DateTimeKind.Utc) };
        var atLogin = VehicleExpiryWatch.ToServerWallClockOle(player.LoginTime);

        Assert.Equal(atLogin, watch.EvaluatedSince(player));
        Assert.Equal(atLogin, watch.EvaluatedSince(player)); 

        watch.MarkEvaluated(player, atLogin + 0.01);
        Assert.Equal(atLogin + 0.01, watch.EvaluatedSince(player));

        
        var relogged = new PlayerState { SessionId = 7, LoginTime = player.LoginTime.AddHours(1) };
        Assert.Equal(VehicleExpiryWatch.ToServerWallClockOle(relogged.LoginTime), watch.EvaluatedSince(relogged));

        watch.MarkEvaluated(relogged, atLogin + 0.5);
        watch.Retain(Array.Empty<PlayerState>());
        Assert.Equal(VehicleExpiryWatch.ToServerWallClockOle(relogged.LoginTime), watch.EvaluatedSince(relogged));
    }

    [Fact]
    public void Watch_UsesTheExpiriesClock()
    {
        
        var before = ServerWallClock.Now().DateTime.ToOADate();
        var login = VehicleExpiryWatch.ToServerWallClockOle(DateTime.UtcNow);
        var after = ServerWallClock.Now().DateTime.ToOADate();
        Assert.InRange(login, before - 1.0 / 86400, after + 1.0 / 86400);
    }

    private static void AssertTick(string retail, IReadOnlyList<OwnedVehicle> owned, double since, double now)
    {
        var due = VehicleExpiryNotifier.Due(owned, since, now);
        var record = Assert.Single(due);
        Assert.Equal(retail, Convert.ToHexStringLower(VehiclePackets.BuildVehicleListUpdate2838(new[] { record })));
    }

    private static double Server(int y, int mo, int d, int h, int mi, int s, int ms)
        => new DateTime(y, mo, d, h, mi, s, ms).ToOADate();

    private static string Hex(double value) => Convert.ToHexStringLower(BitConverter.GetBytes(value));
}
