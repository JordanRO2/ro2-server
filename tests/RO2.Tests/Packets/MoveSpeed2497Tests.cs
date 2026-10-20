using System;
using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Correctness gate for RMI 2497 OnChangeActorMoveSpeed: byte-exact output against a reference frame, the compact-length first byte
/// requirement, and refusal of speeds &lt;= 0.
/// </summary>
/// <remarks>
/// The reference body is a PC-handle frame: 01 10 10 00 fc e3 00 00 00 00 11 c7 eb 06 00 00 c0 40, which decodes to handle
/// (low byte 0x11, a PC) and speed 6.0. The value 6.0 is the usual world-entry speed, the value that
/// <see cref="WorldEntryService"/> sends through <c>TraitPackets.PcModalWorldEntrySpeed</c>.
///
/// This complements <see cref="ActorMoveSpeed2497Tests"/>, which gates the constant prefix and the field offsets. This file asserts
/// the whole body against the reference frame and covers the speed-at-or-below-zero guard, which that file does not.
/// </remarks>
public class MoveSpeed2497Tests
{
    
    private const string RealFrameHex = "01101000fce30000000011c7eb060000c040";
    private const uint RealHandle = 0x06EBC711u;
    private const float RealSpeed = 6.0f;

    [Fact]
    public void Build_ReproducesARealCapturedFrame_ByteForByte()
    {
        var expected = Convert.FromHexString(RealFrameHex);
        var actual = TraitPackets.BuildActorMoveSpeed2497(RealHandle, RealSpeed);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ModalWorldEntrySpeed_IsSix_AndMatchesTheCapturedSpeed()
    {
        Assert.Equal(6.0f, TraitPackets.PcModalWorldEntrySpeed);
        
        var expected = Convert.FromHexString(RealFrameHex);
        var actual = TraitPackets.BuildActorMoveSpeed2497(RealHandle, TraitPackets.PcModalWorldEntrySpeed);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Build_RefusesSpeedLessThanOrEqualToZeroOrNonFinite(float badSpeed)
    {
        
        
        
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TraitPackets.BuildActorMoveSpeed2497(RealHandle, badSpeed));
    }
}
