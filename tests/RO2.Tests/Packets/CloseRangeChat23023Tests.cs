using System.Buffers.Binary;
using RO2.Server.Chat.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 23023 ReqChatCloseRangeChatRoom -> 23542 [u16 0] and the 23529 echo (ReqChatCloseRangeHandler). Each request
/// is answered on the sender's session with 23542 0000 and, in the same tick, a 23529 equal to the request.
/// </summary>
public class CloseRangeChat23023Tests
{
    [Theory]
    [InlineData("21000000d39161d689cb48458f5ee0a209bdd0de10008e34f0a7b3b6e00d033a00290001f8")]      
    [InlineData("23000000d5c9a9098356134b81309b68564ae93410009932eaa7b3b6e00d0455007700550001f8")]  
    [InlineData("35000000471e6eadbc3b3c4ca2efe760fee2a62d10000c74630dc161a6370d6c00650062006900680069006e002000620061006e00670001f8")] 
    public void Retail_lines_are_echoed_when_sent_under_the_senders_own_uid(string hex)
    {
        var body = Convert.FromHexString(hex);
        ulong sender = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(ReqChatCloseRangeHandler.SenderOffset));

        Assert.Equal(ReqChatCloseRangeHandler.Verdict.Accepted, ReqChatCloseRangeHandler.Check(body, true, sender));
        Assert.Equal(ReqChatCloseRangeHandler.Verdict.WrongSender, ReqChatCloseRangeHandler.Check(body, true, sender ^ 1));
        Assert.Equal(ReqChatCloseRangeHandler.Verdict.NotLoggedIn, ReqChatCloseRangeHandler.Check(body, false, 0));
        Assert.Equal(0x0010, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));   
    }

    [Fact]
    public void Answer_23542_is_the_dumps_frame()
    {
        Assert.Equal(Convert.FromHexString("0000"), ReqChatCloseRangeHandler.AcceptedAnswer());
    }

    [Theory]
    [InlineData("21000000d39161d689cb48458f5ee0a209bdd0de10008e34f0a7b3b6e00d033a002900")]          
    [InlineData("21000000d39161d689cb48458f5ee0a209bdd0de10008e34f0a7b3b6e00d033a00290001f800")]    
    [InlineData("20000000d39161d689cb48458f5ee0a209bdd0de10008e34f0a7b3b6e00d033a00290001f8")]      
    [InlineData("1b000000d39161d689cb48458f5ee0a209bdd0de10008e34f0a7b3b6e00d00")]                  
    [InlineData("")]
    public void Malformed_lines_are_not_echoed(string hex)
    {
        var body = Convert.FromHexString(hex);

        Assert.Equal(ReqChatCloseRangeHandler.Verdict.Malformed, ReqChatCloseRangeHandler.Check(body, true, 0x0DE0B6B3A7F0348EUL));
    }
}
