using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash-class gate for the trade subsystem's enveloped S2C opcodes. Every body the trade handlers emit must begin with a
/// valid compact-length marker from {1, 2, 4, 8}. A raw body is rejected by the client's compact-length decoder, which
/// leaves the per-opcode handler with a null buffer and crashes the client.
/// </summary>
/// <remarks>
/// Covers the wrapped opcodes 2382 OnBeginTradePC, 2386 OnDragGold and 2387 OnLockTradePC. The three large trade opcodes
/// (2384 OnDragTradePC, 2385 OnDelTradePC, 2344 OnBankItemSwap) carry full 126-byte item records and are suppressed rather
/// than built, so they have no builder to gate here.
/// </remarks>
public class TradeCrashSweepTests
{
    private static readonly byte[] ValidMarkers = { 1, 2, 4, 8 };

    [Fact]
    public void OnBeginTradePC_ReproducesRealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString("010c0c00b3e2000000001136f306");
        var body = TradePackets.BuildOnBeginTradePC(0x06F33611u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnDragGold_ReproducesRealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString("01161600bbe20000000000001115f0060000000000000000");
        var body = TradePackets.BuildOnDragGold(0x06F01511u, gold: 0);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void OnLockTradePC_ReproducesRealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString("010e0e00bde200000000001115f00601");
        var body = TradePackets.BuildOnLockTradePC(0x06F01511u, bothLocked: false);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData(0x00100011u)]
    [InlineData(0xDEADBE11u)]
    [InlineData(0u)]
    public void EveryWrapBuilderStartsWithAValidMarkerAndSelfLenInvariant(uint handle)
    {
        AssertCompactBAInvariant(TradePackets.BuildOnBeginTradePC(handle));
        AssertCompactBAInvariant(TradePackets.BuildOnDragGold(handle, gold: 123456));
        AssertCompactBAInvariant(TradePackets.BuildOnLockTradePC(handle, bothLocked: true));
    }

    [Fact]
    public void MsgTagsAreInTheMeasuredBand()
    {
        
        
        AssertMsgTagInBand(TradePackets.BuildOnBeginTradePC(0x06F33611u), 58035);
        AssertMsgTagInBand(TradePackets.BuildOnDragGold(0x06F01511u, 0), 58043);
        AssertMsgTagInBand(TradePackets.BuildOnLockTradePC(0x06F01511u, false), 58045);
    }

    private static void AssertCompactBAInvariant(byte[] body)
    {
        
        Assert.Contains(body[0], ValidMarkers);

        
        int markerWidth = body[0];
        int envelope = 1 + markerWidth;                 
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1 + markerWidth));
        int payloadLen = body.Length - envelope - 2;     
        Assert.Equal(payloadLen + 2, selfLen);
    }

    private static void AssertMsgTagInBand(byte[] body, ushort expectedTag)
    {
        int markerWidth = body[0];
        int tagOffset = 1 + markerWidth + 2;             
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tagOffset));
        Assert.Equal(expectedTag, tag);
        Assert.InRange(tag, (ushort)52001, (ushort)58526);
    }
}
