using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout tests for the constant exit acknowledgements: RMI 2305 (logout), 2938 (both exit paths) and 2312 (character switch).</summary>
/// <remarks>Logout (1302) is usually answered by 2305 and 2938; a character switch (1418) is usually answered by 2938 and 2312. Each acknowledgement body is a fixed constant. Without an acknowledgement the client waits, so every exit path must send one.</remarks>
public class SessionExitPacketsTests
{
    [Fact]
    public void DisconnectNotify2305_MatchesTheRetailFrameByteForByte()
    {
        Assert.Equal(Convert.FromHexString("010a0a00f5d2000000000000"),
            SessionExitPackets.DisconnectNotify2305);
    }

    [Fact]
    public void ChangePc2312_MatchesTheRetailFrameByteForByte()
    {
        Assert.Equal(Convert.FromHexString("010a08051ec6f77f00000000"),
            SessionExitPackets.ChangePc2312);
    }

    [Fact]
    public void ChangePc2312_IsTheDocumentedEnvelopeException()
    {
        
        
        
        var b = SessionExitPackets.ChangePc2312;
        Assert.Equal(0x01, b[0]);
        Assert.Equal(10, b[1]);
        Assert.Equal(b.Length - 2, b[1]);
        Assert.Equal(1288, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2)));
        var second = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4));
        Assert.Equal(50718, second);
        Assert.True(second < 52001, "blob+2 must stay below the msgTag space for 2312");
    }

    [Fact]
    public void TheThreeBodiesAreDistinct()
    {
        
        
        Assert.NotEqual(SessionExitPackets.DisconnectNotify2305, SessionExitPackets.ChangePc2312);
        Assert.NotEqual(SessionExitPackets.DisconnectNotify2305, SessionExitPackets.Ack2938);
        
        Assert.Equal(SessionExitPackets.DisconnectNotify2305.Length,
            SessionExitPackets.ChangePc2312.Length);
    }
}
