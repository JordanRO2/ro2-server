using System.Buffers.Binary;
using RO2.Network.Protocol;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout tests for the two server-time encodings: RMI 2669 NfyServerTimeToLoginPC (u64 LE, 1e6 units per second) and RMI 2811 NfyServerTime (u32 LE, seconds).</summary>
/// <remarks>The client's reader is Boost-based, and an invalid instant makes it throw and hang. The encoding must therefore follow the real clock, not a fixed value.</remarks>
public class ServerTimeTests
{
    
    private const ulong Retail2669Micros = 212649800652731329UL;
    private static readonly byte[] Retail2669Body =
        Convert.FromHexString("c1b376f5dd7bf302");

    
    private const uint Retail2811UnixSeconds = 1782997455U;
    private static readonly byte[] Retail2811Body =
        Convert.FromHexString("cf61466a00000000");

    [Fact]
    public void JulianOffset_IsMidnightNotNoon()
    {
        
        
        
        Assert.Equal(2_440_588L * 86_400L, ServerTimeCodec.JulianMidnightToUnixSeconds);
        Assert.Equal(210_866_803_200L, ServerTimeCodec.JulianMidnightToUnixSeconds);
    }

    [Fact]
    public void Retail2669Body_DecodesToItsCaptureInstant()
    {
        
        Assert.Equal(Retail2669Micros,
            BinaryPrimitives.ReadUInt64LittleEndian(Retail2669Body));

        
        
        var when = ServerTimeCodec.FromClientMicroseconds(Retail2669Micros);
        Assert.Equal(2026, when.UtcDateTime.Year);
        Assert.Equal(7, when.UtcDateTime.Month);
        Assert.Equal(2, when.UtcDateTime.Day);
    }

    [Fact]
    public void Encode2669_RoundTripsAndReproducesTheRetailFrame()
    {
        var captured = ServerTimeCodec.FromClientMicroseconds(Retail2669Micros);

        
        
        var reencoded = ServerTimeCodec.ToClientMicroseconds(captured);
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, reencoded);

        Assert.Equal(Retail2669Micros / 1000UL, reencoded / 1000UL);
        Assert.Equal(Retail2669Body.AsSpan(2).ToArray(), body.AsSpan(2).ToArray());
    }

    [Fact]
    public void Encode2669_TopBytesMatchRetailsConstantHighBits()
    {
        
        
        
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            body, ServerTimeCodec.ToClientMicroseconds(DateTimeOffset.UtcNow));
        Assert.Equal(0xF3, body[6]);
        Assert.Equal(0x02, body[7]);
    }

    [Fact]
    public void Build2811Body_IsUnixSecondsThenZeroDword()
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(Retail2811UnixSeconds);
        var body = ServerTimeCodec.BuildServerTimeBody(when);

        
        Assert.Equal(Retail2811Body, body);
        Assert.Equal(8, body.Length);
        Assert.Equal(Retail2811UnixSeconds, BinaryPrimitives.ReadUInt32LittleEndian(body));
        
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public void BothEncodings_AgreeOnTheSameInstant()
    {
        
        
        
        var from2669 = ServerTimeCodec.FromClientMicroseconds(Retail2669Micros);
        var from2811 = DateTimeOffset.FromUnixTimeSeconds(Retail2811UnixSeconds);

        var delta = (from2811 - from2669).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(10),
            $"2669 decoded to {from2669:O} but 2811 decoded to {from2811:O} "
            + $"({delta.TotalSeconds:F1}s apart); the two encodings disagree.");
    }

}
