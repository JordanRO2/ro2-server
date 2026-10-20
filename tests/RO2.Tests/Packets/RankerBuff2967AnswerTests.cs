using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 2967 AnsRankerBuff, NONE family, 6 bytes: [u32 buffId][u16 result]. The success answer has result 0, and the refusal results are covered as well. The frames are built byte for byte.
/// </summary>
public class RankerBuff2967AnswerTests
{
    [Theory]
    
    [InlineData(50606u, RankerBuffHandler.ResultOk, "aec500000000")]
    
    [InlineData(50606u, RankerBuffHandler.ResultNotEnoughZeny, "aec500000100")]
    
    
    [InlineData(50601u, RankerBuffHandler.ResultNotAStatue, "a9c500001b52")]
    public void Answer_IsRetailsSixBytes(uint buffId, ushort result, string retailHex)
    {
        var body = RankerBuffHandler.BuildAnsRankerBuff2967(buffId, result);

        Assert.Equal(retailHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void RefusalCodes_AreTheClientsOwn()
    {
        
        
        Assert.Equal(0, RankerBuffHandler.ResultOk);
        Assert.Equal(1, RankerBuffHandler.ResultNotEnoughZeny);
        Assert.Equal(21019, RankerBuffHandler.ResultNotAStatue);
    }
}
