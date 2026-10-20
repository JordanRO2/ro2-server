using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>RMI 1393 ReqMailSend and 2445 OnMailSend refusals, verified against reference frames.</summary>
public class MailSendTests
{
 /// <summary>A send to a mailbox that already holds 31 mails.</summary>
    private const string MailboxFullRequestHex =
        "0173730059e3117d0b0a6d00750073006100760061006e0064006100000000000000000000000000000000000000000000"
        + "0000005300700069006e0065006c00200052006500700061006900720020004b00690074002000280032000000000000"
        + "000000000099ecea0da83d040e0b020201000000";

 /// <summary>The refusal: 16204 "Mailbox is full.", followed by zero bytes.</summary>
    private const string MailboxFullAnswerHex = "011d1d005ae3117d0b0a4c3f00000000000000000000000000000000000000";

 /// <summary>A send to a recipient name with a trailing space.</summary>
    private const string TrailingSpaceRequestHex =
        "0173730059e311ec050e4e0061006e0058006900200000000000000000000000000000000000000000000000000000000000"
        + "00004300610070007400610069006e0020005600610064006f006e00200043006100720064002000280000000000000000"
        + "000000f5ca5a0ea83d040e15040501000000";

 /// <summary>The refusal: 16202 "Can't find the target player."</summary>
    private const string TrailingSpaceAnswerHex = "011d1d005ae311ec050e4a3f00000000000000000000000000000000000000";

    [Fact]
    public void MailboxFullRefusalMatchesRetailByteForByte()
    {
        var body = MailPackets.BuildMailSendFailed2445(0x0A0B7D11u, MailPackets.SendMailboxFull);
        Assert.Equal(Convert.FromHexString(MailboxFullAnswerHex), body);
        Assert.Equal((ushort)16204, MailPackets.SendMailboxFull);
    }

    [Fact]
    public void TargetNotFoundRefusalMatchesRetailByteForByte()
    {
        var body = MailPackets.BuildMailSendFailed2445(0x0E05EC11u, MailPackets.SendTargetNotFound);
        Assert.Equal(Convert.FromHexString(TrailingSpaceAnswerHex), body);
    }

    [Fact]
    public void ParsesTheRetailRequestThatHitAFullMailbox()
    {
        Assert.True(MailPackets.TryParseSend(Convert.FromHexString(MailboxFullRequestHex), out var req));
        Assert.Equal(0x0A0B7D11u, req.OwnerHandle);
        Assert.Equal("musavanda", req.RecipientName);
        Assert.Equal("Spinel Repair Kit (2", req.Title);
        Assert.Equal(0L, req.Gold);
        Assert.Equal(0x0E043DA80DEAEC99UL, req.ItemUid);
        Assert.Equal(2u, req.ItemCount);
        Assert.Equal(string.Empty, req.Body);
    }

    [Fact]
    public void KeepsTheRecipientExactlyAsTyped()
    {
        
        Assert.True(MailPackets.TryParseSend(Convert.FromHexString(TrailingSpaceRequestHex), out var req));
        Assert.Equal("NanXi ", req.RecipientName);
        Assert.Equal("Captain Vadon Card (", req.Title);
        Assert.Equal(5u, req.ItemCount);
    }

    [Fact]
    public void TheLimitIsTheSmallestBoxRetailRefusedAt()
    {
        
        Assert.Equal(31, MailPackets.MailboxPlayerSendLimit);
        Assert.Equal(30, MailPackets.MailLifetimeDays);
    }
}
