using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2762 AnsOpenCloseRangeChatRoom refused with result 28: a 1533 received while the requester's own close-range
/// room is still open (<see cref="OpenCloseRangeChatRoomHandler"/>).
/// </summary>
/// <remarks>
/// The registry is process-wide, so every test uses its own random character ids and GUIDs.
/// </remarks>
public class CloseRangeChat2762RefusalTests
{
 /// <summary>Reference 136-byte refusal body: [1c 00] followed by 134 zero bytes.</summary>
    private static readonly string LiveRefusal = "1c00" + new string('0', 134 * 2);

    [Fact]
    public void Refusal_IsTheRetailBody()
    {
        Assert.Equal(OpenCloseRangeChatRoomHandler.AnswerBytes * 2, LiveRefusal.Length);
        var body = OpenCloseRangeChatRoomHandler.BuildRefusal2762(OpenCloseRangeChatRoomHandler.ResultAlreadyOwnsOpenRoom);
        Assert.Equal(LiveRefusal, Convert.ToHexString(body).ToLowerInvariant());
        
        Assert.StartsWith("1c000000000000000000000000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void OwnRoomOpen_RefusesTheSecondRoom_UntilTheOwnerLeavesIt()
    {
        
        
        var (owner, guid) = OpenRoom();
        Assert.True(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(owner));

        Assert.True(CloseRangeChatRooms.ResolveExit(guid, owner.CharacterId) is CloseRangeOwnerExited);
        Assert.False(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(owner));
    }

    [Fact]
    public void RoomOfAnEarlierLogin_DoesNotBlockTheRelog()
    {
        
        
        
        var (owner, _) = OpenRoom();
        var relogged = new PlayerState { CharacterId = owner.CharacterId, EntityId = owner.EntityId + 1 };
        Assert.False(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(relogged));

        var fresh = Guid.NewGuid();
        CloseRangeChatRooms.Register(new CloseRangeRoom(fresh, relogged.CharacterId, relogged.EntityId, 10));
        Assert.True(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(relogged));
        Assert.False(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(owner));
    }

    [Fact]
    public void AnotherCharactersRoom_IsNotTheRequestersOwn()
    {
        
        
        var (owner, _) = OpenRoom();
        var other = new PlayerState { CharacterId = owner.CharacterId + 1, EntityId = owner.EntityId + 1 };
        Assert.False(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(other));
    }

    [Fact]
    public void HandedOffRoom_BelongsToTheNewMaster()
    {
        
        
        var (owner, guid) = OpenRoom();
        var member = new PlayerState { CharacterId = owner.CharacterId + 1, EntityId = owner.EntityId + 1 };

        Assert.True(CloseRangeChatRooms.ResolveExit(guid, owner.CharacterId, member.CharacterId, member.EntityId)
            is CloseRangeMasterHandedOff);

        Assert.False(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(owner));
        Assert.True(OpenCloseRangeChatRoomHandler.OwnsOpenRoom(member));
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, member.CharacterId) is CloseRangeOwnerExited);
    }

 /// <summary>A room registered as the 1533 handler registers it, for a fresh character.</summary>
    private static (PlayerState Owner, byte[] Guid) OpenRoom()
    {
        var owner = new PlayerState
        {
            CharacterId = 0x40000000u | (uint)Random.Shared.Next(1, 0x3FFFFFF0),
            EntityId = 0x00100000u | (uint)Random.Shared.Next(1, 0xFFFF),
        };
        var guid = Guid.NewGuid().ToByteArray();
        CloseRangeChatRooms.Register(new CloseRangeRoom(CloseRangeChatRooms.IdOf(guid), owner.CharacterId,
            owner.EntityId, 10, new byte[102], HasPassword: false));
        return (owner, guid);
    }
}
