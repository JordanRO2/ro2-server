using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Covers the duplicate-entry guard at login (LoginPCHandler.IsEntryInFlight) and the whole-second cut applied to RMI 2522
/// at character load (VehicleRules.CutToWholeSeconds).
/// </summary>
public class LoginEntryAndVehicleReauditTests
{
    
    [Fact]
    public void Duplicate1417_IsDropped_OnlyWhileTheSameEntryIsUnacked()
    {
        var inFlight = new PlayerState { CharacterId = 77, MapLoaded = false };
        Assert.True(LoginPCHandler.IsEntryInFlight(inFlight, 77));
        Assert.False(LoginPCHandler.IsEntryInFlight(inFlight, 78));                                     
        Assert.False(LoginPCHandler.IsEntryInFlight(new PlayerState { CharacterId = 77, MapLoaded = true }, 77)); 
        Assert.False(LoginPCHandler.IsEntryInFlight(null, 77));                                         
    }

    
    private const uint Reins90190000 = 90190000;
    private const double Sum11f60e6a = 46273.57421875; 

    [Fact]
    public void CharacterLoad_CutsTheSumToTheWholeSecond_Archive15115f3f()
    {
        
        
        
        var stored = new List<OwnedVehicle> { new() { RidingId = Reins90190000, ExpiryOle = Sum11f60e6a } };
        var cut = VehicleRules.CutToWholeSeconds(stored);
        Assert.NotSame(stored, cut);
        Assert.Equal(Hex("1cddf35f3298e640"), BitConverter.GetBytes(cut[0].ExpiryOle));
        Assert.Equal(Sum11f60e6a, stored[0].ExpiryOle); 

        
        var nowOle = new DateTime(2026, 7, 20, 15, 45, 51).ToOADate();
        var body = VehiclePackets.BuildAllVehicleList2522(cut, nowOle);
        Assert.Equal(Hex("110000000100" + "b03060051cddf35f3298e640010200"), body);
    }

    [Fact]
    public void Cut_LeavesPermanentAndWholeSecondRecordsUntouched()
    {
        
        var whole = BitConverter.ToDouble(Hex("1cddf3df3d0ee540"));
        var stored = new List<OwnedVehicle>
        {
            new() { RidingId = 90140000, ExpiryOle = 0 },
            new() { RidingId = Reins90190000, ExpiryOle = whole },
        };
        var cut = VehicleRules.CutToWholeSeconds(stored);
        Assert.Same(stored, cut); 
        Assert.Equal(BitConverter.DoubleToInt64Bits(whole), BitConverter.DoubleToInt64Bits(cut[1].ExpiryOle));
    }

    [Fact]
    public void GrantInTheSameSession_AddsDaysToTheUncutSum_Archive11f60e6a()
    {
        
        
        var owned = new List<OwnedVehicle> { new() { RidingId = Reins90190000, ExpiryOle = 46228.57421875 } };
        var nowOle = new DateTime(2026, 7, 20, 13, 44, 50).ToOADate();
        var next = VehicleRules.Grant(owned, Reins90190000, 5, nowOle);
        Assert.Equal(Hex("000000603293e640"), BitConverter.GetBytes(next[0].ExpiryOle));
        for (int i = 0; i < 8; i++)
            next = VehicleRules.Grant(next, Reins90190000, 5, nowOle);
        Assert.Equal(Hex("000000603298e640"), BitConverter.GetBytes(next[0].ExpiryOle));
    }
}
