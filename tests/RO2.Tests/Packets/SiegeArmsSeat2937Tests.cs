using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Siege arm seat answer (2937 AnsSiegeArmsUseStart): the success case and the "arm already operated" refusal.
/// </summary>
public class SiegeArmsSeat2937Tests
{
    [Fact]
    public void OccupiedArmRefusal_ReproducesTheRetailFrame()
    {
        
        Assert.Equal(Convert.FromHexString("08000000f75e000000000000"),
            SiegeArmsUseStartHandler.BuildSeatRefusal2937(SiegeArmsUseStartHandler.ResultArmOccupied));
    }

    [Fact]
    public void Success_EchoesTheArm_InTheSameShape()
    {
        
        Assert.Equal(Convert.FromHexString("08000000000013c379020000"),
            EconomyMiscPackets.BuildSiegeArmsUseStartAck(0x0279C313u));
    }
}
