using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2620 AnsPartyDelMsg, the answer to 1459 ReqPartyDelMsg. Neither opcode has a reference frame, so this pins the
/// layout the client's reader defines and is not verified against the reference server. The client copies 11 bytes;
/// the handler acts only when the u16 at offset 0 is 0, reloading the board named by the u16 at offset 2 (1 reloads
/// board 1560, 2 reloads board 1460).
/// </summary>
public class PartyDelMsg2620Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void RecruitBoard_EchoesKindAndGid()
    {
        
        const ulong gid = 0x0DE0B6B3A7B8CC07UL;
        var body = PartyDelMsgHandler.BuildAnsPartyDelMsg2620(PartyDelMsgHandler.KindRecruitBoard, gid);

        Assert.Equal("0000" + "01" + "07ccb8a7b3b6e00d", Hex(body));
    }

    [Theory]
    [InlineData(PartyDelMsgHandler.KindRecruitBoard)]
    [InlineData(PartyDelMsgHandler.KindApplyBoard)]
    public void Body_IsTheElevenBytesTheReaderTakes(byte kind)
    {
        var body = PartyDelMsgHandler.BuildAnsPartyDelMsg2620(kind, 42);

        Assert.Equal(PartyDelMsgHandler.BodyLength, body.Length);
        Assert.Equal(11, body.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body));      
        Assert.Equal(kind, body[2]);                                          
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(3)));
    }

    [Fact]
    public void KindBytes_AreTheBuildersOwn()
    {
        
        Assert.Equal(1, PartyDelMsgHandler.KindRecruitBoard);
        Assert.Equal(2, PartyDelMsgHandler.KindApplyBoard);
    }
}
