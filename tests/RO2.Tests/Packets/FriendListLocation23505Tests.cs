using RO2.Application.Configuration;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Server.Chat;
using RO2.Server.Login.Handlers.Rmi;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Online friend location in 23505 AnsFriendList: the channel label built from the Login (12005) and Zone
/// (1421, 1417) records in ChannelPresenceStore, as the Chat server puts it in the record.
/// </summary>
public class FriendListLocation23505Tests
{
 /// <summary>Server index and regular channel count of the reference server (its 2555 names channels 1 to 10).</summary>
    private static WorldConfig RetailWorld(int channelCount) => new() { ServerIndex = 1, ServerInstance = 12, ChannelCount = channelCount };

    [Fact]
    public void Online_friend_record_carries_its_channel_label_byte_for_byte()
    {
        
        
        var retail = Convert.FromHexString(
            "00003500000001006967f4a7b3b6e00d0847007200650065004c00750063006b0000010504000943006800" +
            "61006e006e0065006c00200031000000");
        var greeLuck = new Character { Id = 0xA7F46769, Name = "GreeLuck", Level = 5, Job = 4 };

        var entry = FriendStore.Entry(greeLuck, online: true, location: "Channel 1")
            with { Uid = CharacterGid.Of(0x0DE0B6B300000000UL, greeLuck.Id) };

        Assert.Equal(retail, CommunityChannels.BuildFriendListBody(new[] { entry }));
    }

    [Fact]
    public void Offline_friend_record_never_carries_a_location()
    {
        
        
        
        var retail = Convert.FromHexString(
            "00005800000002004cab56ab8f3c296f0c690052004f00430072006500630065006e007400690061000001" +
            "321100094300680061006e006e0065006c002000320028044169c6a7b3b6e00d0553004900410044004800" +
            "0000320700002804");
        var online = FriendStore.Entry(new Character { Id = 0xAB56AB4C, Name = "iROCrecentia", Level = 50, Job = 17 },
                online: true, location: "Channel 2")
            with { Uid = 0x6F293C8FAB56AB4CUL, TrailerA = 40, TrailerB = 4 };
        
        var offline = FriendStore.Entry(new Character { Id = 0xA7C66941, Name = "SIADH", Level = 50, Job = 7 },
                online: false, location: "Channel 2")
            with { Uid = 0x0DE0B6B3A7C66941UL, TrailerA = 40, TrailerB = 4 };

        Assert.Equal("", offline.Location);
        Assert.Equal(retail, CommunityChannels.BuildFriendListBody(new[] { online, offline }));
    }

    [Fact]
    public void Online_friend_with_no_recorded_channel_gets_an_empty_location()
    {
        var entry = FriendStore.Entry(new Character { Id = 7, Name = "A", Level = 1, Job = 1 }, online: true, location: null);
        Assert.True(entry.Online);
        Assert.Equal("", entry.Location);
    }

    [Theory]
    
    
    
    [InlineData(0x06514041u, "Channel 1")]
    [InlineData(0x06B141C1u, "Channel 7")]
    [InlineData(0x06D14241u, "Channel 9")]
    [InlineData(0x06E14281u, "Channel 10")]
    
    [InlineData(0x06F14001u, "")]
    public void Zone_labels_a_channel_handle_the_way_its_list_names_it(uint handle, string label)
    {
        Assert.Equal(label, LoginPCHandler.ChannelLocation(RetailWorld(10), handle));
    }

    [Fact]
    public void Zone_gives_no_label_to_a_channel_its_list_does_not_have()
    {
        
        Assert.Equal("", LoginPCHandler.ChannelLocation(RetailWorld(3), 0x06B141C1u));
    }

    [Fact]
    public void Login_names_the_selected_channel_as_retail_12504_does()
    {
        
        
        Assert.Equal("Channel 7", SelectServerHandler.SelectedChannelLabel(RetailWorld(9), 0x06B141C1u));
        
        Assert.Equal("Channel 1", SelectServerHandler.SelectedChannelLabel(RetailWorld(9), 0x06514041u));
        Assert.Equal("", SelectServerHandler.SelectedChannelLabel(RetailWorld(9), 0x06F14001u));
    }

    [Fact]
    public void Login_and_Zone_agree_on_every_channel_label()
    {
        
        
        var world = RetailWorld(10);
        for (int ch = 1; ch <= world.ChannelCount; ch++)
        {
            uint handle = ZoneChannelList.ChannelId(world, ch);
            Assert.Equal(ChannelPresenceStore.ChannelLabel(ch), SelectServerHandler.SelectedChannelLabel(world, handle));
            Assert.Equal(ChannelPresenceStore.ChannelLabel(ch), LoginPCHandler.ChannelLocation(world, handle));
        }
    }
}
