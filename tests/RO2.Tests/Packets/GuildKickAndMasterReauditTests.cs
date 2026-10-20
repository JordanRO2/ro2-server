using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>Guild kick notices: the master's contribution notice (1431 to 2730) and the not-master answer (1585 to 2854).</summary>
public class GuildKickAndMasterReauditTests
{
    
    
    
    [Theory]
    
    
    [InlineData(325968518L, "86e26d1300000000")]
    [InlineData(326085612L, "ecab6f1300000000")]
    [InlineData(326144159L, "9f90701300000000")]
    [InlineData(0L, "0000000000000000")]
    public void Retail2730_KickFrames(long total, string hex)
        => Assert.Equal(hex, ToHex(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(total)));

    [Fact]
    public async Task KickHandover_AddsTheKickedMembersContributionToTheMaster()
    {
        
        
        
        var master = new GuildMember { CharId = 1, Name = "Master", AuthId = 0, ContribPoints = 325968518 };
        var kicked = new GuildMember { CharId = 2, Name = "Kicked", AuthId = 1, ContribPoints = 117094 };
        var guild = new Guild { Id = 987654321, Name = "A4", MasterCharId = 1, Members = new List<GuildMember> { master } };

        var total = await GuildService.Instance.HandOverContributionAsync(guild, kicked);

        Assert.Equal(326085612L, total);
        Assert.Equal("ecab6f1300000000", ToHex(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(total!.Value)));
    }

    [Fact]
    public async Task KickHandover_WithNothingToMove_StillGivesTheTotal()
    {
        
        var master = new GuildMember { CharId = 1, Name = "Master", AuthId = 0, ContribPoints = 83295 };
        var kicked = new GuildMember { CharId = 2, Name = "Kicked", AuthId = 1, ContribPoints = 0 };
        var guild = new Guild { Id = 987654322, Name = "A4b", MasterCharId = 1, Members = new List<GuildMember> { master } };

        var total = await GuildService.Instance.HandOverContributionAsync(guild, kicked);

        Assert.Equal("5f45010000000000", ToHex(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(total!.Value)));
    }

    
    
    
    [Fact]
    public void Retail2854_NotMaster_Is19()
    {
        
        
        Assert.True(GuildRenameRules.TryParse(Hex(
            "24000000f505bc0da83d040e0d00480065006c006c00270073002000470061007200640065006e00"), out _, out var name));
        Assert.Equal("Hell's Garden", name);
        Assert.Equal(GuildRenameRules.Ok, GuildRenameRules.CheckFormat(name));
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, GuildCanRenameHandler.ResultNotMaster);
        Assert.Equal("1300", ToHex(body));
        Assert.NotEqual(GuildRenameRules.NameNotUsable, GuildCanRenameHandler.ResultNotMaster);
    }

    [Fact]
    public void RenameFormat_KeepsLatin1Leads_AsRetailGuildsHaveThem()
    {
        
        
        Assert.Equal(GuildRenameRules.Ok, GuildRenameRules.CheckFormat("ßlueßlooD"));
        Assert.Equal(GuildRenameRules.NameNotUsable, GuildRenameRules.CheckFormat("-Æsir"));
    }
}
