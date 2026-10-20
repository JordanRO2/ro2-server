using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Layout tests for request 1580 ReqPreViewCacheItem and response 2846 AnsPreViewCacheItem. The request is a 20-byte
/// blob. The response is a 128-byte blob, of which only the u16 at offset 0 is consumed; the rest is not read.
/// </summary>
public class PreViewCacheItem2846Tests
{
    [Fact]
    public void Request1580_ReadsTwoBagSlotUidRecords()
    {
        
        
        var body = Convert.FromHexString(
            "0107" + "8877665544332211"
            + "0003" + "0102030405060708");

        Assert.True(PreViewCacheItemHandler.TryParseRequest1580(body, out var req));

        Assert.Equal(1, req.BagA);
        Assert.Equal(7, req.SlotA);
        Assert.Equal(0x1122334455667788UL, req.UidA);
        Assert.Equal(0, req.BagB);
        Assert.Equal(3, req.SlotB);
        Assert.Equal(0x0807060504030201UL, req.UidB);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    public void Request1580_RejectsAnyLengthButTheReaders20(int length)
    {
        Assert.False(PreViewCacheItemHandler.TryParseRequest1580(new byte[length], out _));
    }

    [Theory]
    [InlineData(PreViewCacheItemHandler.ResultItemMissing, "0200")]   
    [InlineData(PreViewCacheItemHandler.ResultCannotExtend, "0300")]  
    public void Refusal2846_IsTheResultAndAnEmpty126ByteRecord(ushort result, string head)
    {
        var body = PreViewCacheItemHandler.BuildRefusal2846(result);

        Assert.Equal(128, body.Length);
        Assert.Equal(head, Convert.ToHexString(body.AsSpan(0, 2)).ToLowerInvariant());
        Assert.All(body[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Refusal2846_NeverCarriesResultZero()
    {
        
        
        Assert.Throws<ArgumentOutOfRangeException>(() => PreViewCacheItemHandler.BuildRefusal2846(0));
    }
}
