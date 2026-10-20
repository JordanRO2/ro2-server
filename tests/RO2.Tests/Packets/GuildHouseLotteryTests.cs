using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Guild-house lottery request and answer pairs: 1686 to 3018, 1687 to 3019 and 1690 to 3021.</summary>
public class GuildHouseLotteryTests
{
    
    
    
    private const string Retail1686Lottery3 = "0300000001000000";
    private const string Retail1686Lottery1x255 = "01000000ff000000";

    
    
    private const string Retail3018 = "2f5f";

    
    
    private const string Retail3019Empty = "00000100";

    
    
    private const string Retail3019Open = "0000010201000000a086010003000000a0860100";

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Request1686ReadsLotteryAndCount()
    {
        Assert.True(GuildHouseLotteryHandler.TryReadRequest(Convert.FromHexString(Retail1686Lottery3), out var id, out var count));
        Assert.Equal((3u, 1u), (id, count));
        Assert.True(GuildHouseLotteryHandler.TryReadRequest(Convert.FromHexString(Retail1686Lottery1x255), out id, out count));
        Assert.Equal((1u, 255u), (id, count));
        Assert.Equal(GuildHouseLotteryHandler.BodyLength, Retail1686Lottery3.Length / 2);
        Assert.False(GuildHouseLotteryHandler.TryReadRequest(new byte[7], out _, out _));
    }

    [Fact]
    public void Refusal3018ToANonMasterIsRetailsBytes()
    {
        var guild = new Guild { Id = 1, Name = "G", MasterCharId = 100 };
        var code = GuildHouseLotteryHandler.RefusalFor(guild, 200);
        Assert.Equal(GuildHouseLotteryHandler.ResultNotGuildMaster, code);
        Assert.Equal(Retail3018, Hex(GuildHouseLotteryHandler.BuildAnsGuildHouseLottery3018(code!.Value)));
    }

    [Fact]
    public void Refusal3018ByGuildState()
    {
        
        Assert.Equal((ushort)25207, GuildHouseLotteryHandler.RefusalFor(null, 200));
        Assert.Equal("7762", Hex(GuildHouseLotteryHandler.BuildAnsGuildHouseLottery3018(25207)));
        
        var guild = new Guild { Id = 1, Name = "G", MasterCharId = 100 };
        Assert.Null(GuildHouseLotteryHandler.RefusalFor(guild, 100));
    }

    [Fact]
    public void Ticket3019EmptyIsRetailsClosedAnswer()
        => Assert.Equal(Retail3019Empty, Hex(GuildHouseTicketHandler.BuildAnsGuildHouseTicket3019Empty()));

    [Fact]
    public void Ticket3019OpenFormSharesTheHeader()
    {
        
        var ours = GuildHouseTicketHandler.BuildAnsGuildHouseTicket3019Empty();
        Assert.Equal(Retail3019Open[..6], Hex(ours)[..6]);
    }

    [Fact]
    public void Rank3021EmptyFollowsTheReader()
    {
        
        
        var body = GuildHouseLotteryRankHandler.BuildAnsGuildHouseLotteryRank3021Empty();
        Assert.Equal("000001000100", Hex(body));
        var p = 2;
        for (var list = 0; list < 2; list++)
        {
            var width = body[p];
            Assert.Equal(1, width);
            var n = body[p + 1];
            p += 1 + width + n * 112;
        }
        Assert.Equal(body.Length, p);
    }

    [Fact]
    public void OpcodesAreTheBinarysIds()
    {
        Assert.Equal(1686, ZoneC2SOpcodes.ReqGuildHouseLottery);
        Assert.Equal(1687, ZoneC2SOpcodes.ReqGuildHouseTicket);
        Assert.Equal(1690, ZoneC2SOpcodes.ReqGuildHouseLotteryRank);
        Assert.Equal(3018, ZoneS2COpcodes.AnsGuildHouseLottery);
        Assert.Equal(3019, ZoneS2COpcodes.AnsGuildHouseTicket);
        Assert.Equal(3021, ZoneS2COpcodes.AnsGuildHouseLotteryRank);
    }
}
