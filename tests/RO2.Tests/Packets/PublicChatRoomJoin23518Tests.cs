using RO2.Server.Chat;
using RO2.Server.Chat.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Layout of RMI 23518 AnsPublicChatRoomJoin, the answer to request 23012 (<see cref="ReqPublicChatRoomJoinHandler"/>).
/// On success the body is a u16 result followed by a 16-byte record. Any other result shows the message
/// "Cannot find channel room."
/// </summary>
public class PublicChatRoomJoin23518Tests
{
    [Fact]
    public void Joined_IsResultZeroThenTheChannelGuid()
    {
        var body = ReqPublicChatRoomJoinHandler.BuildAnswer(ReqPublicChatRoomJoinHandler.ResultJoined, CommunityChannels.Trade.Guid);
        Assert.Equal(ReqPublicChatRoomJoinHandler.BodyLength, body.Length);
        Assert.Equal(new byte[] { 0x00, 0x00 }, body[..2]);
        Assert.Equal(CommunityChannels.Trade.Guid, body[2..]);
    }

    [Fact]
    public void Joined_HasTheShapeOfTheSibling23519()
    {
        
        foreach (var ch in CommunityChannels.All)
            Assert.Equal(PrivateChatRooms.BuildExitAnswer(ch.Guid), ReqPublicChatRoomJoinHandler.BuildAnswer(0, ch.Guid));
    }

    [Fact]
    public void UnknownChannel_IsResultTenThenTheEchoedGuid()
    {
        var guid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var body = ReqPublicChatRoomJoinHandler.BuildAnswer(ReqPublicChatRoomJoinHandler.ResultChannelNotFound, guid);
        Assert.Equal(new byte[] { 0x0A, 0x00 }, body[..2]);
        Assert.Equal(guid, body[2..]);
        Assert.Null(CommunityChannels.ByGuid(guid));
    }

    [Fact]
    public void Guid_MustBeSixteenBytes()
    {
        Assert.Throws<ArgumentException>(() => ReqPublicChatRoomJoinHandler.BuildAnswer(0, new byte[15]));
    }
}
