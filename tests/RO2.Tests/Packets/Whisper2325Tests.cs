using System.Text;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-identity gate for the whisper S2C message (2325 OnReceiveWhisper), checked against a reference exchange.
/// </summary>
/// <remarks>
/// The reference exchange is a whisper from one player to another with the text "whisper". Both the C2S 1316
/// ReqPCWhisper and the S2C 2325 OnReceiveWhisper decode with zero trailing bytes (see WhisperHandler for the field map).
/// The test extracts the raw recipient-name and message spans from the reference C2S, feeds them with the reference ids
/// and sender name into <see cref="WhisperHandler.BuildWhisperBlob"/>, and asserts that the produced 2325 body equals
/// the reference body (everything after the 2-byte header) byte for byte.
/// </remarks>
public class Whisper2325Tests
{
    
    
    private const string C2sHex =
        "2b000124053300000000009ee2118dc2050c690052004f00430072006500630065006e007400690061000800770068006900730070006500720001f8";
    private const string S2cHex =
        "0115094900000000009fe2118cc2050000000944006f0046006f006300750073004d0065000c690052004f00430072006500630065006e007400690061000800770068006900730070006500720001f8";

    private static byte[] FromHex(string h)
    {
        var b = new byte[h.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
        return b;
    }

    [Fact]
    public void BuildWhisperBlob_FromCapturedC2S_MatchesCapturedS2C()
    {
        
        var s2cWire = FromHex(S2cHex);
        Assert.Equal(0x01, s2cWire[0]);
        Assert.Equal(2325, BitConverter.ToUInt16(s2cWire, 1));
        var expectedBody = s2cWire[3..];
        Assert.Equal(77, expectedBody.Length);

        
        var c2sWire = FromHex(C2sHex);
        
        Assert.Equal(0x01, c2sWire[2]);
        Assert.Equal(1316, BitConverter.ToUInt16(c2sWire, 3));
        var c2sBody = c2sWire[5..];

        int p = 0;
        uint Read32() { var v = BitConverter.ToUInt32(c2sBody, p); p += 4; return v; }

        uint innerLen = Read32();
        Assert.Equal((uint)(c2sBody.Length - 4), innerLen); 
        uint id1 = Read32();                                
        uint id2 = Read32();                                
        byte targetCount = c2sBody[p]; p += 1;
        var recipRaw = c2sBody[p..(p + targetCount * 2)]; p += targetCount * 2;
        ushort msgCount = BitConverter.ToUInt16(c2sBody, p); p += 2;
        var messageRaw = c2sBody[p..(p + msgCount * 2)]; p += msgCount * 2;
        Assert.Equal(c2sBody.Length, p); 

        
        var senderRaw = Encoding.Unicode.GetBytes("DoFocusMe");

        
        Assert.Equal(0xe29e0000u, id1); 
        Assert.Equal(0x05c28d11u, id2); 
        Assert.Equal(12, targetCount);  
        Assert.Equal(8, msgCount);      

        
        
        
        uint s2cId1 = BitConverter.ToUInt32(FromHex("00009fe2"), 0);
        uint s2cId2 = BitConverter.ToUInt32(FromHex("118cc205"), 0);

        byte[] actualBody = WhisperHandler.BuildWhisperBlob(
            s2cId1, s2cId2, senderRaw, recipRaw, messageRaw);

        
        Assert.Equal(expectedBody, actualBody);
    }
}
