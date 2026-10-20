using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout tests for RMI 2440 OnSavePoint and its 1309 request.</summary>
/// <remarks>Regression test: the client's compact-length decoder accepts only a first byte of 1, 2, 4 or 8, so a raw position body crashes it. The reply must use the compact-length layout.</remarks>
public class SavePoint2440Tests
{
    
    private const string RetailBodyHex = "010e0e0003e311a5190200009a019a01";

    
    
    private const string RealRequestHex = "010E0E0002E30200100013220000" + "0F00" + "4E0465A6";

    [Fact]
    public void ReproducesARealRetailReplyByteForByte()
    {
        var expected = Convert.FromHexString(RetailBodyHex);
        
        var actorHandle = BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(6));
        var result = BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(10));
        var prev = BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(12));
        var idx = BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(14));

        var body = SetBindIndexHandler.BuildOnSavePoint2440(actorHandle, result, prev, idx);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void ReplyCarriesTheCompactEnvelopeThatTheClientRequires()
    {
        var body = SetBindIndexHandler.BuildOnSavePoint2440(0x00100002u, 0, 0, 15);

        
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(SetBindIndexHandler.BindBodyLen, body.Length);

        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(blob.Length, body[1]);                                       
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));  
        Assert.Equal(SetBindIndexHandler.MsgTagOnSavePoint,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));               
    }

    [Fact]
    public void ReplyNeverCarriesMapOrCoordinates()
    {
        
        
        
        var body = SetBindIndexHandler.BuildOnSavePoint2440(0x00100002u, 0, 3, 15);
        Assert.Equal(16, body.Length);
    }

    [Fact]
    public void EchoesTheHandleAndIndexFromTheRequest()
    {
        var req = Convert.FromHexString(RealRequestHex);
        Assert.True(SetBindIndexHandler.TryParseRequest(req, out var handle, out var idx));
        Assert.Equal(0x00100002u, handle);
        Assert.Equal(15, idx);

        var body = SetBindIndexHandler.BuildOnSavePoint2440(handle, 0, 0, idx);
        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(idx, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(12)));
    }

    [Fact]
    public void RequestParsingToleratesTheTrailingTransportBytes()
    {
        
        
        var req = Convert.FromHexString(RealRequestHex);
        Assert.Equal(20, req.Length);                       
        Assert.True(SetBindIndexHandler.TryParseRequest(req, out _, out _));

        
        Assert.True(SetBindIndexHandler.TryParseRequest(req.AsSpan(0, 16), out _, out var idx));
        Assert.Equal(15, idx);
    }

    [Theory]
    [InlineData("")]
    [InlineData("010E0E0002E3")]      
    [InlineData("030E0E0002E30200100013220000 0F00")]  
    public void MalformedRequestsAreRejectedRatherThanAnswered(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        Assert.False(SetBindIndexHandler.TryParseRequest(bytes, out _, out _));
    }
}
