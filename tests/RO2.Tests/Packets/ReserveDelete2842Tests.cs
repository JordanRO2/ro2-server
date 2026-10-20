using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Tests the 1577 ReqReserveDeletePC request and its 2842 AnsReserveDeletePC answer.</summary>
public class ReserveDelete2842Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Success_MatchesRetailFrame()
    {
        
        var body = ReserveDeleteHandler.BuildAnsReserveDeletePC(0x0DE0B6B3A7A4D143UL,
            ReserveDeleteHandler.ResultDeleted, DateTimeOffset.FromUnixTimeSeconds(0x6A4F8E5F));

        Assert.Equal("43d1a4a7b3b6e00d00005f8e4f6a00000000", Hex(body));
    }

    [Fact]
    public void GuildMasterRefusal_MatchesRetailFrame()
    {
        
        var body = ReserveDeleteHandler.BuildAnsReserveDeletePC(0x0DE0B6B3A7A565B6UL,
            ReserveDeleteHandler.ResultGuildMaster, DateTimeOffset.FromUnixTimeSeconds(0x6A4F8E71));

        Assert.Equal("b665a5a7b3b6e00dd23b718e4f6a00000000", Hex(body));
    }

    [Fact]
    public void GuildMaster_IsTheGuildsMasterOnly()
    {
        var guild = new Guild { MasterCharId = 12 };
        guild.Members.Add(new GuildMember { CharId = 12 });
        guild.Members.Add(new GuildMember { CharId = 13 });

        Assert.True(ReserveDeleteHandler.IsGuildMaster(guild, 12));
        Assert.False(ReserveDeleteHandler.IsGuildMaster(guild, 13));
        Assert.False(ReserveDeleteHandler.IsGuildMaster(null, 12));
    }
}
