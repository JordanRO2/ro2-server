using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Fall damage: the damage rule, the HP outcome, and the wire layout of a survived and a fatal fall
/// (1565 ReqFallDamage, 2815 NfyFallDamage), plus the killerless 2392 that closes a fall death.
/// </summary>
public class FallDamage2815Tests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void SurvivedFall_ReproducesTheLocalCaptureFrame()
    {
        
        Assert.Equal(Hex("0114140070e411d8a90a11d8a90ab0b80000454a0000"),
            FallDamagePackets.BuildFallDamage2815(0x0AA9D811u, hpAfter: 47280, damage: 19013));
    }

    [Fact]
    public void FatalFall_ReproducesTheReferenceFrame()
    {
        
        Assert.Equal(Hex("0114140070e411a3830b11a3830b0000000005ac0100"),
            FallDamagePackets.BuildFallDamage2815(0x0B83A311u, hpAfter: 0, damage: 109573));
    }

    [Fact]
    public void FallDeath_DeadNotifyHasNoKillerAndCarriesTheFallDamage()
    {
        
        Assert.Equal(Hex("01141400cbe20000000011a3830b0000000005ac0100"),
            ActorVitalsPackets.BuildDeadNotify2392(0x0B83A311u, killerHandle: 0, lethalDamage: 109573));
    }

    [Fact]
    public void RetailRule_MatchesBothLocalCaptureFalls()
    {
        Assert.Equal(19013, FallDamagePackets.RetailFallDamage(15.7360f, 66293));
        Assert.Equal(14536, FallDamagePackets.RetailFallDamage(14.3855f, 66293));
    }

    [Fact]
    public void RetailRule_ChargesNothingAtOrBelowTheSafeHeight()
    {
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(9.963f, 66293));
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(10f, 66293));
        Assert.Equal(0, FallDamagePackets.RetailFallDamage(float.NaN, 66293));
    }

    [Fact]
    public void ApplyFallDamage_KillsAtZeroAndNeverClampsAtOne()
    {
        Assert.Equal((70, false), FallDamagePackets.ApplyFallDamage(100, 30));
        Assert.Equal((0, true), FallDamagePackets.ApplyFallDamage(100, 100));
        
        Assert.Equal((0, true), FallDamagePackets.ApplyFallDamage(99899, 177132));
        Assert.Equal((1, false), FallDamagePackets.ApplyFallDamage(2, 1));
    }

    [Fact]
    public void ApplyFallDamage_NoDamageOrAlreadyDead_ChangesNothing()
    {
        Assert.Equal((100, false), FallDamagePackets.ApplyFallDamage(100, 0));
        Assert.Equal((0, false), FallDamagePackets.ApplyFallDamage(0, 500));
    }
}
