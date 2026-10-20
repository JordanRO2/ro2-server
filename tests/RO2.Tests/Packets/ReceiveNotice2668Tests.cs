using System.Buffers.Binary;
using System.Text;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Layout tests for S2C 2668 OnReceiveNotice (<see cref="NoticePackets.Build2668"/>). No reference frame exists for this opcode, so these pin the layout the client reader expects.</summary>
public class ReceiveNotice2668Tests
{
    [Fact]
    public void PlainNotice_IsU32LengthThenTheReaderBlob()
    {
        var body = NoticePackets.Build2668("Hi", 0xFEEE00, NoticePackets.PlainNotice, 0, 0);

        
        Assert.Equal(Convert.FromHexString(
            "15000000" + "1500" + "0000" + "00000000" + "00" + "0200" + "48006900" + "feee0000" + "00" + "00"),
            body);
    }

    [Fact]
    public void BlobLength_Is17PlusTwicetheChars_AndInnerSizeEqualsIt()
    {
        const string text = "Server maintenance in 10 minutes.";
        var body = NoticePackets.Build2668(text, 0xFFFFFF, NoticePackets.PlainNotice, 0, 0);

        int blobLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.Equal(17 + 2 * text.Length, blobLen);
        Assert.Equal(4 + blobLen, body.Length);
        Assert.Equal(blobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        
        Assert.Equal(text.Length, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4 + 9)));
        Assert.Equal(text, Encoding.Unicode.GetString(body, 4 + 11, 2 * text.Length));
    }

    [Fact]
    public void NumericParamNotice_PutsTypeAt8_AndBothParamsLast()
    {
        var body = NoticePackets.Build2668("@ @", 0x102030, NoticePackets.NumericParamNotice, 7, 9);
        var blob = body.AsSpan(4);

        Assert.Equal(12, blob[8]);                      
        int colour = 11 + 2 * 3;
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x00 }, blob.Slice(colour, 4).ToArray()); 
        Assert.Equal(7, blob[colour + 4]);              
        Assert.Equal(9, blob[colour + 5]);              
        Assert.Equal(colour + 6, blob.Length);
    }

    [Fact]
    public void LongText_IsCutSoInnerSizeFitsAU16()
    {
        var body = NoticePackets.Build2668(new string('x', 40_000), 0, NoticePackets.PlainNotice, 0, 0);

        int blobLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.Equal(17 + 2 * NoticePackets.MaxChars, blobLen);
        Assert.True(blobLen <= ushort.MaxValue);
        Assert.Equal(blobLen, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
    }

 /// <summary>
 /// The GM /notice command (the live caller of BroadcastNoticeAsync): the colour is the six hex digits the
 /// GM types, the text is the rest of the line with its spacing kept.
 /// </summary>
    [Theory]
    [InlineData("/notice FFD700 Server restart in 10 minutes", 0xFFD700u, "Server restart in 10 minutes")]
    [InlineData("/NOTICE #00ff00   two  spaces kept ", 0x00FF00u, "two  spaces kept")]
    public void NoticeCommand_ParsesColourAndText(string line, uint rgb, string text)
    {
        Assert.True(AnnouncementService.TryParseNoticeCommand(line, out var gotRgb, out var gotText));
        Assert.Equal(rgb, gotRgb);
        Assert.Equal(text, gotText);
    }

 /// <summary>No colour, a malformed colour or no text is refused: no default colour is invented.</summary>
    [Theory]
    [InlineData("/notice")]
    [InlineData("/notice FFD700")]
    [InlineData("/notice FFD700   ")]
    [InlineData("/notice Hello world")]
    [InlineData("/notice FFF Hello")]
    [InlineData("/notice GGGGGG Hello")]
    [InlineData("/noticex FFD700 Hello")]
    public void NoticeCommand_RejectsMissingColourOrText(string line)
    {
        Assert.False(AnnouncementService.TryParseNoticeCommand(line, out _, out _));
    }
}

/// <summary>Layout tests for three other answers in this group, which also lack a reference frame. Each pins the shape the client reader expects.</summary>
public class NoFrameReaderShapeTests
{
 /// <summary>Layout of 2308 OnDeleteCharacterACK: a variable-length blob whose u16 at blob offset 8 selects the result.</summary>
    [Theory]
    [InlineData(CharacterDeleteHandler.ResultDeleted, "0000")]
    [InlineData(CharacterDeleteHandler.ResultCannotDelete, "c83b")]
    [InlineData(CharacterDeleteHandler.ResultGuildMaster, "d23b")]
    public void DeleteCharacterAck2308_IsCompactBlobWithResultAt8(ushort result, string resultHex)
    {
        var body = CharacterDeleteHandler.BuildOnDeleteCharacterAck(0x00421B24, result);

        
        Assert.Equal(Convert.FromHexString("010a" + "0a00" + "c7da" + "241b4200" + resultHex), body);
    }

 /// <summary>Layout of 2892 AnsCharNameList: a compact count followed by that many wide strings. An empty list is a compact 0.</summary>
    [Fact]
    public void AnsCharNameList2892_EmptyIsCompactZero()
        => Assert.Equal(new byte[] { 0x01, 0x00 }, CharNameListHandler.BuildEmptyAnsCharNameList());

 /// <summary>Layout of 2491 OnGetEffectList: a length-prefixed payload holding a u16 count followed by that many 19-byte entries.</summary>
    [Fact]
    public void GetEffectList2491_EmptyIsLength6TargetCount0()
        => Assert.Equal(Convert.FromHexString("06000000" + "11d70a06" + "0000"),
            EffectListHandler.BuildEmptyEffectList(0x060AD711));
}
