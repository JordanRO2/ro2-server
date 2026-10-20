using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using RO2.Domain.Entities;
using RO2.Network.ProudNet;
using RO2.Server.Chat;
using RO2.Server.Chat.Handlers.Rmi;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Community-channel membership (23012/23518, 23013/23519, 23528 receivers), the close-range exit refusal
/// (23022/23541 result 17) and the 1550 answer throttle.
/// </summary>
public class CommunityChannelReauditTests
{
    private static ProudSession NewSession(uint id) =>
        new(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), id, NullLogger.Instance);

    
    [Fact]
    public async Task Membership_StartsEmpty_LoginJoinsAllThree()
    {
        await using var s = NewSession(80001);
        foreach (var ch in CommunityChannels.All) Assert.False(CommunityChannelMembership.IsMember(s, ch.Index));

        
        CommunityChannelMembership.JoinAll(s);
        foreach (var ch in CommunityChannels.All) Assert.True(CommunityChannelMembership.IsMember(s, ch.Index));
    }

    [Fact]
    public async Task ChannelLeave_TakesOnlyThatChannelOut_AndALaterLoginRejoinsIt()
    {
        await using var s = NewSession(80002);
        CommunityChannelMembership.JoinAll(s);

        Assert.True(CommunityChannelMembership.Leave(s, CommunityChannels.Public.Index));
        Assert.False(CommunityChannelMembership.Leave(s, CommunityChannels.Public.Index));
        Assert.False(CommunityChannelMembership.IsMember(s, CommunityChannels.Public.Index));
        Assert.True(CommunityChannelMembership.IsMember(s, CommunityChannels.Trade.Index));
        Assert.True(CommunityChannelMembership.IsMember(s, CommunityChannels.LookingForParty.Index));

        CommunityChannelMembership.JoinAll(s);
        Assert.True(CommunityChannelMembership.IsMember(s, CommunityChannels.Public.Index));
    }

    [Fact]
    public void ChannelLeaveAnswer23519_IsRetailsShape()
    {
        
        
        var guid = Convert.FromHexString("18bdb29a61b6ef49904c1e201b547f1f");
        Assert.Equal(Convert.FromHexString("000018bdb29a61b6ef49904c1e201b547f1f"), PrivateChatRooms.BuildExitAnswer(guid));
    }

    [Fact]
    public async Task Join23518_UnknownThenAlreadyThenJoined()
    {
        
        
        await using var s = NewSession(80003);
        CommunityChannelMembership.JoinAll(s);

        Assert.Equal(ReqPublicChatRoomJoinHandler.ResultChannelNotFound, ReqPublicChatRoomJoinHandler.Decide(s, null));
        Assert.Equal(ReqPublicChatRoomJoinHandler.ResultAlreadyParticipating,
            ReqPublicChatRoomJoinHandler.Decide(s, CommunityChannels.Trade));

        CommunityChannelMembership.Leave(s, CommunityChannels.Trade.Index);
        Assert.Equal(ReqPublicChatRoomJoinHandler.ResultJoined, ReqPublicChatRoomJoinHandler.Decide(s, CommunityChannels.Trade));
        Assert.True(CommunityChannelMembership.IsMember(s, CommunityChannels.Trade.Index));
        Assert.Equal(ReqPublicChatRoomJoinHandler.ResultAlreadyParticipating,
            ReqPublicChatRoomJoinHandler.Decide(s, CommunityChannels.Trade));
    }

    [Fact]
    public void Join23518_AlreadyParticipating_IsTwentyThreeThenTheGuid()
    {
        var body = ReqPublicChatRoomJoinHandler.BuildAnswer(ReqPublicChatRoomJoinHandler.ResultAlreadyParticipating,
            CommunityChannels.Public.Guid);
        Assert.Equal(new byte[] { 0x17, 0x00 }, body[..2]);
        Assert.Equal(CommunityChannels.Public.Guid, body[2..]);
    }

    
    private static readonly byte[] RetailRefusedRoom = Convert.FromHexString("9af33e14c721c040ae039240274921ce");

    [Fact]
    public void ExitAnswer23541_ReproducesRetailsRefusalAndSuccess()
    {
        
        Assert.Equal(Convert.FromHexString("11009af33e14c721c040ae039240274921ce"),
            CloseRangeExitCheck.BuildAnswer(CloseRangeExitCheck.ResultNotParticipating, RetailRefusedRoom));
        Assert.Equal(Convert.FromHexString("0000d09f3364d611284b83385e9cc180c7e5"),
            CloseRangeExitCheck.BuildAnswer(CloseRangeExitCheck.ResultLeft, Convert.FromHexString("d09f3364d611284b83385e9cc180c7e5")));
    }

    [Fact]
    public void Decide_LoneOwnerExitIsAccepted_TheDuplicateAfterItsSignalIsRefused()
    {
        const uint owner = 7001;
        var open = Signal(1_000, CloseRangeRoomSignals.OpenKind, RetailRefusedRoom, owner);
        Assert.Equal(CloseRangeExitCheck.Verdict.Accept, CloseRangeExitCheck.Decide(open, [], owner));

        
        var exit = Signal(1_001, CloseRangeRoomSignals.ExitKind, RetailRefusedRoom, owner);
        Assert.Equal(CloseRangeExitCheck.Verdict.Refuse, CloseRangeExitCheck.Decide(open, [exit], owner));
    }

    [Fact]
    public void Decide_MemberByItsLastSignal()
    {
        const uint owner = 7101, member = 7102, stranger = 7103;
        var open = Signal(2_000, CloseRangeRoomSignals.OpenKind, RetailRefusedRoom, owner);
        var join = Signal(2_001, CloseRangeRoomSignals.JoinKind, RetailRefusedRoom, member);
        var left = Signal(2_002, CloseRangeRoomSignals.ExitKind, RetailRefusedRoom, member);
        var otherRoomJoin = Signal(2_003, CloseRangeRoomSignals.JoinKind, Guid.NewGuid().ToByteArray(), stranger);

        Assert.Equal(CloseRangeExitCheck.Verdict.Accept, CloseRangeExitCheck.Decide(open, [join], member));
        Assert.Equal(CloseRangeExitCheck.Verdict.Refuse, CloseRangeExitCheck.Decide(open, [join, left], member));
        Assert.Equal(CloseRangeExitCheck.Verdict.Refuse, CloseRangeExitCheck.Decide(open, [join, otherRoomJoin], stranger));
        Assert.Equal(CloseRangeExitCheck.Verdict.NoRecord, CloseRangeExitCheck.Decide(null, [], member));
    }

    [Fact]
    public void Decide_HandOffMakesTheNewMasterTheOwner()
    {
        const uint a = 7201, b = 7202;
        var open = Signal(3_000, CloseRangeRoomSignals.OpenKind, RetailRefusedRoom, a);
        var join = Signal(3_001, CloseRangeRoomSignals.JoinKind, RetailRefusedRoom, b);
        var handOff = Signal(3_002, CloseRangeRoomSignals.ExitKind, RetailRefusedRoom, a, newMaster: b);
        Assert.Equal(CloseRangeExitCheck.Verdict.Accept, CloseRangeExitCheck.Decide(open, [join, handOff], b));
        Assert.Equal(CloseRangeExitCheck.Verdict.Refuse, CloseRangeExitCheck.Decide(open, [join, handOff], a));
    }

    [Fact]
    public void TryClaim_RefusesASecondExitInsideTheWindow()
    {
        var room = Guid.NewGuid().ToByteArray();
        const uint who = 7301;
        long t0 = 1_000_000;
        Assert.True(CloseRangeExitCheck.TryClaim(room, who, t0));
        Assert.False(CloseRangeExitCheck.TryClaim(room, who, t0 + 250));        
        Assert.True(CloseRangeExitCheck.TryClaim(room, who + 1, t0 + 250));     
        Assert.True(CloseRangeExitCheck.TryClaim(room, who, t0 + CloseRangeExitCheck.RecentExitMs));

        var accepted = Guid.NewGuid().ToByteArray();
        CloseRangeExitCheck.RecordAccepted(accepted, who, t0);
        Assert.False(CloseRangeExitCheck.TryClaim(accepted, who, t0 + 1));
    }

    private static CloseRangeRoomSignal Signal(int timestamp, string kind, byte[] guid, uint character, uint newMaster = 0) => new()
    {
        Id = ObjectId.GenerateNewId(timestamp).ToString(),
        Kind = kind,
        RoomGuid = guid,
        CharacterId = character,
        NewMasterCharacterId = newMaster,
    };

    
    [Theory]
    [InlineData(null, 0L, false)]  
    [InlineData(0L, 400L, true)]   
    [InlineData(0L, 484L, true)]
    [InlineData(0L, 499L, true)]
    [InlineData(0L, 500L, false)]
    [InlineData(0L, 536L, false)]  
    public void GiftPage1550_IsDroppedUnder500msAfterTheLastAnswered(long? last, long now, bool dropped)
    {
        Assert.Equal(dropped, AccountGiftListHandler.IsThrottled(last, now));
    }
}
