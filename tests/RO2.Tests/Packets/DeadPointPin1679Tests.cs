using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1679 ReqUpdateDeadPoint pins a dead character at the reported position. While the character
/// is dead, a 1526 is relayed only at that pin.
/// </summary>
public class DeadPointPin1679Tests
{
    
    
    private static readonly byte[] Retail1679 = Convert.FromHexString("1ce246010000000057836844e15f5244a3a5434322");

    
    
    private static readonly byte[] RetailPinned1526 = Convert.FromHexString(
        "11c68908" +
        "000000000000000000000000000000000000000000000000000000000000000000000000" +
        "57836844e15f5244a3a5434357836844e15f5244a3a54343e58ec931a952b83d000200020000000000ea8ec931ba7a80759211dd01");

    
    
    
    private static readonly byte[] RetailOtherDeath1679 = Convert.FromHexString("1ce2460100000000172e6044b4ff2644e668434322");
    private static readonly byte[] RetailOffPin1526 = Convert.FromHexString(
        "11a54c0a" +
        "0000000000000000000057bd0000000000000000000000000000000000000000000057bd" +
        "f83f60440029274434684343f83f60440029274434684343a5702a000eea2ec0400200020000000000ce702a00bd7a5f746514dd01");

    private static PlayerState DeadAt(uint x, uint y, uint z) => new()
    {
        IsDead = true,
        X = BitConverter.UInt32BitsToSingle(x),
        Y = BitConverter.UInt32BitsToSingle(y),
        Z = BitConverter.UInt32BitsToSingle(z),
    };

    private static (float X, float Y, float Z) PointOf1679(byte[] body) => (
        BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(UpdateDeadPointHandler.PointOffset)),
        BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(UpdateDeadPointHandler.PointOffset + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(UpdateDeadPointHandler.PointOffset + 8)));

 /// <summary>The current x/y position carried by the 1526.</summary>
    private static (float X, float Y) CurrentOf1526(byte[] body) => (
        BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(40)),
        BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(44)));

    [Fact]
    public void RetailFrame_Is21Bytes_WithThePointAt8()
    {
        Assert.Equal(UpdateDeadPointHandler.BodyLength, Retail1679.Length);
        var (x, y, z) = PointOf1679(Retail1679);
        Assert.Equal(0x44688357u, BitConverter.SingleToUInt32Bits(x));
        Assert.Equal(0x44525FE1u, BitConverter.SingleToUInt32Bits(y));
        Assert.Equal(0x4343A5A3u, BitConverter.SingleToUInt32Bits(z));
    }

    [Fact]
    public void ADeadPc_IsMovedToThe1679Point_BitForBit_AsRetailsLater2315Shows()
    {
        var player = DeadAt(0x4468864Bu, 0x44527116u, 0x4343CE4Fu);   
        var (x, y, z) = PointOf1679(Retail1679);

        Assert.True(UpdateDeadPointHandler.TryPinDeadPoint(player, x, y, z));

        
        Assert.Equal(0x44688357u, BitConverter.SingleToUInt32Bits(player.X));
        Assert.Equal(0x44525FE1u, BitConverter.SingleToUInt32Bits(player.Y));
        Assert.Equal(0x4343A5A3u, BitConverter.SingleToUInt32Bits(player.Z));
        Assert.True(player.IsDead);
    }

    [Fact]
    public void ALivePc_OrANonFinitePoint_ChangesNothing()
    {
        var alive = new PlayerState { IsDead = false, X = 1f, Y = 2f, Z = 3f };
        var (x, y, z) = PointOf1679(Retail1679);
        Assert.False(UpdateDeadPointHandler.TryPinDeadPoint(alive, x, y, z));
        Assert.Equal((1f, 2f, 3f), (alive.X, alive.Y, alive.Z));

        var dead = DeadAt(0x3F800000u, 0x40000000u, 0x40400000u);   
        Assert.False(UpdateDeadPointHandler.TryPinDeadPoint(dead, float.NaN, y, z));
        Assert.False(UpdateDeadPointHandler.TryPinDeadPoint(dead, x, float.PositiveInfinity, z));
        Assert.Equal((1f, 2f, 3f), (dead.X, dead.Y, dead.Z));
    }

    [Fact]
    public void WhileDead_OnlyA1526AtThePin_IsRelayed()
    {
        
        var pinned = DeadAt(0x4468864Bu, 0x44527116u, 0x4343CE4Fu);
        var (px, py, pz) = PointOf1679(Retail1679);
        UpdateDeadPointHandler.TryPinDeadPoint(pinned, px, py, pz);
        var (cx, cy) = CurrentOf1526(RetailPinned1526);
        Assert.True(ViewGroupMoveHandler.IsAtDeadPoint(pinned, cx, cy));

        
        var other = DeadAt(0x445FD6A8u, 0x442635BDu, 0x43436C86u);
        var (ox, oy, oz) = PointOf1679(RetailOtherDeath1679);
        UpdateDeadPointHandler.TryPinDeadPoint(other, ox, oy, oz);
        var (qx, qy) = CurrentOf1526(RetailOffPin1526);
        Assert.False(ViewGroupMoveHandler.IsAtDeadPoint(other, qx, qy));
    }

    [Fact]
    public void ThePinComparesXAndYOnly_ZDriftAtThePinIsStillRelayed()
    {
        
        var pinned = DeadAt(0x44688357u, 0x44525FE1u, 0x4343A5A3u);
        var (cx, cy) = CurrentOf1526(RetailPinned1526);
        pinned.Z += 0.01f;
        Assert.True(ViewGroupMoveHandler.IsAtDeadPoint(pinned, cx, cy));
        Assert.False(ViewGroupMoveHandler.IsAtDeadPoint(pinned, BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(cx) + 1), cy));
    }
}
