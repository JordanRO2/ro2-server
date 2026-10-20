using System.Buffers.Binary;
using System.Text;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1315 ReqPCChatting -> 2324 OnReceiveChat: the body (<see cref="ChatPackets"/>) and who receives it
/// (<see cref="ChatHandler"/>).
/// </summary>
/// <remarks>
/// Every pair below is a request and the expected 2324 echo, chosen where the u32 trailer is 0 so the whole
/// body can be compared. The client ignores that trailer, so system lines are compared up to it.
/// </remarks>
public class Chat2324RoutingTests
{
    public static TheoryData<string, string, uint, string> RetailEchoes => new()
    {
        // Normal (0).
        {
            "2100000000009ce2114def06000b006900200061006d002000620075006c006c00790001f8",
            "iROCrecentia", 0u,
            "3e00000000009de2114def060c690052004f00430072006500630065006e00740069006100000b006900200061006d002000620075006c006c00790001f800000000"
        },
        // Party (1). Handle 0x00026915 is the party handle for party id 0x269.
        {
            "3f00000000009ce2110efc06011a0069006d0020006f006e0020006f007400680065007200670061006d00650020007200690067006800740020006e006f00770001f8",
            "7B", 0x269u,
            "4800000000009de2156902000237004200011a0069006d0020006f006e0020006f007400680065007200670061006d00650020007200690067006800740020006e006f00770001f800000000"
        },
        // Guild (2).
        {
            "2700000000009ce2114e2a07020e006f0068002c0020007400680061006e006b00200079006f00750001f8",
            "Namazuko", 0u,
            "3c00000000009de2114e2a07084e0061006d0061007a0075006b006f00020e006f0068002c0020007400680061006e006b00200079006f00750001f800000000"
        },
        // Area (3).
        {
            "1d00000000009ce211a615080309005400720061006e0073006d006f00670001f8",
            "DontFocusMe", 0u,
            "3800000000009de211a615080b44006f006e00740046006f006300750073004d0065000309005400720061006e0073006d006f00670001f800000000"
        },
        // Raid (7). Party handle 0x0004a615.
        {
            "2700000000009ce211760e07070e004800610076006e0020006300680061006e006e0065006c003f0001f8",
            "Namazuko", 0x4A6u,
            "3c00000000009de215a60400084e0061006d0061007a0075006b006f00070e004800610076006e0020006300680061006e006e0065006c003f0001f800000000"
        },
    };

    [Theory]
    [MemberData(nameof(RetailEchoes))]
    public void EchoReproducesRetailByteForByte(string requestHex, string senderName, uint partyId, string retailEchoHex)
    {
        var request = Convert.FromHexString(requestHex);
        Assert.True(ChatHandler.TryReadReqPCChatting(request, out var channel, out var msg));

        // Party and Raid lines carry the party handle (partyId != 0 on those rows); the other channels the
        // sender's own PC handle, the 1315's blob+4 (body+8), which is the sender's PlayerState.EntityId.
        uint handle = partyId != 0
            ? PartyAnswerPackets.PartyHandle(new PartyState { PartyId = partyId })
            : BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8));

        var body = ChatPackets.BuildOnReceiveChat2324(handle, Encoding.Unicode.GetBytes(senderName), channel, msg,
            innerSizeIsLength: false);

        Assert.Equal(Convert.FromHexString(retailEchoHex), body);
    }

    [Theory]
    [InlineData(">> PC(427.81,356.66) Target(414.64,356.45) <<")]
    [InlineData(">> PC(803.15,1244.67) Target(802.13,1249.42) <<")]
    public void SystemLineHasTheRetailEnvelopeAndTheSystemSender(string text)
    {
        // The envelope keeps the standard shape (innerSize == blob length, handle 0, chatType 11, no terminator on
        // the text); only the SENDER is customized: SystemSenderName is "[SYSTEM]: " rather than a bare "SYSTEM",
        // so this verifies the structure plus the custom name rather than a byte-exact frame.
        var body = ChatPackets.BuildSystemLine2324(text);
        var nameLen = ChatPackets.SystemSenderName.Length;

        Assert.Equal(body.Length - 4, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));   // innerSize
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));                 // handle 0
        Assert.Equal((byte)nameLen, body[4 + 8]);                                                  // senderChars
        Assert.Equal(ChatPackets.SystemSenderName,
            Encoding.Unicode.GetString(body.AsSpan(4 + 8 + 1, 2 * nameLen)));                       // the custom name
        Assert.Equal(ChatPackets.ChannelSystem, body[4 + 8 + 1 + 2 * nameLen]);                    // chatType 11
        Assert.Equal(text.Length, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4 + 8 + 1 + 2 * nameLen + 1)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 4)));   // trailer
    }

    [Fact]
    public void RequestReaderKeepsTheClientsOwnUnitsAndRefusesBrokenBodies()
    {
        // "i am bully" + the client's colour code U+F801.
        var request = Convert.FromHexString("2100000000009ce2114def06000b006900200061006d002000620075006c006c00790001f8");
        Assert.True(ChatHandler.TryReadReqPCChatting(request, out var channel, out var msg));
        Assert.Equal(ChatPackets.ChannelNormal, channel);
        Assert.Equal("i am bully", Encoding.Unicode.GetString(msg));

        Assert.False(ChatHandler.TryReadReqPCChatting(request.AsSpan(0, request.Length - 1), out _, out _));
        var empty = (byte[])request.Clone();
        empty[13] = 0; empty[14] = 0; // msgLen 0
        Assert.False(ChatHandler.TryReadReqPCChatting(empty, out _, out _));
        Assert.False(ChatHandler.TryReadReqPCChatting(request.AsSpan(0, 14), out _, out _));
    }

    [Fact]
    public void NameIsCappedWhereTheClientsReaderStops()
    {
        // The client clamps nameLen to 20 and reads that many units; a longer count would misalign the rest.
        var body = ChatPackets.BuildOnReceiveChat2324(0x11u, Encoding.Unicode.GetBytes(new string('a', 25)),
            ChatPackets.ChannelNormal, Encoding.Unicode.GetBytes("hi"), innerSizeIsLength: false);

        Assert.Equal(ChatPackets.MaxNameUnits, body[12]);
        Assert.Equal(ChatPackets.ChannelNormal, body[13 + 2 * ChatPackets.MaxNameUnits]);
        Assert.Equal(body.Length - 4, (int)BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    [Fact]
    public void PartyLineReachesEveryOnlineMemberOfANormalParty()
    {
        var party = new PartyState { PartyId = 7, PartyType = 0, MemberSessionIds = [1, 2, 3] };
        party.FrameSlots[1] = 1; party.FrameSlots[2] = 2; party.FrameSlots[3] = 3;
        Func<uint, bool> online = key => key != 3;

        Assert.Equal(new uint[] { 1, 2 }, ChatHandler.PartyAudience(party, 1, ChatPackets.ChannelParty, online));
        Assert.Equal(new uint[] { 1, 2 }, ChatHandler.PartyAudience(party, 2, ChatPackets.ChannelRaid, online));
    }

    [Fact]
    public void InARaidPartyChatStaysInTheSendersFrameGroupAndRaidChatDoesNot()
    {
        // Frame slots 1-5 are group 0, 6-10 group 1, 11-15 group 2 (the raid view's (slot-1)/5).
        var party = new PartyState { PartyId = 9, PartyType = 2, MemberSessionIds = [10, 11, 12, 13, 14] };
        party.FrameSlots[10] = 1; party.FrameSlots[11] = 5; party.FrameSlots[12] = 6;
        party.FrameSlots[13] = 10; party.FrameSlots[14] = 11;
        Func<uint, bool> online = _ => true;

        Assert.Equal(new uint[] { 10, 11 }, ChatHandler.PartyAudience(party, 11, ChatPackets.ChannelParty, online));
        Assert.Equal(new uint[] { 12, 13 }, ChatHandler.PartyAudience(party, 12, ChatPackets.ChannelParty, online));
        Assert.Equal(new uint[] { 14 }, ChatHandler.PartyAudience(party, 14, ChatPackets.ChannelParty, online));
        Assert.Equal(new uint[] { 10, 11, 12, 13, 14 }, ChatHandler.PartyAudience(party, 11, ChatPackets.ChannelRaid, online));
    }

    [Theory]
    [InlineData((byte)0, -1)]
    [InlineData((byte)1, 0)]
    [InlineData((byte)5, 0)]
    [InlineData((byte)6, 1)]
    [InlineData((byte)20, 3)]
    public void FrameGroupIsSlotMinusOneOverFive(byte slot, int group)
        => Assert.Equal(group, ChatHandler.FrameGroup(slot));

    [Fact]
    public void GuildLineReachesEveryMemberTheSenderIncluded()
    {
        var guild = new Guild { Id = 3 };
        guild.Members.Add(new GuildMember { CharId = 5 });
        guild.Members.Add(new GuildMember { CharId = 6 });
        guild.Members.Add(new GuildMember { CharId = 7 });

        Assert.Equal(new uint[] { 5, 6, 7 }, ChatHandler.GuildAudience(guild));
    }
}
