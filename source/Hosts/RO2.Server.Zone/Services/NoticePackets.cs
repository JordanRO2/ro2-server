using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// S2C 2668 OnReceiveNotice: a server announcement shown as a timed system notification and as a chat line,
/// in the colour chosen by the server.
/// </summary>
/// <remarks>
/// <para>Body: [u32 blobLen] followed by a blob with this layout:</para>
/// <code>
/// +0 u16 innerSize (= blobLen)
/// +2 u16 msgTag (0)
/// +4 u32 handle (0)
/// +8 u8 noticeType (12 = the text carries two numeric parameters)
/// +9 u16 nChars, then nChars UTF-16 units
/// +11+2n u8 R, u8 G, u8 B, u8 (unused)
/// +15+2n u8 param
/// +16+2n u8 param
/// </code>
/// <para>blobLen = 17 + 2·nChars.</para>
/// </remarks>
public static class NoticePackets
{
 /// <summary>noticeType of a plain text notice.</summary>
    public const byte PlainNotice = 0;

 /// <summary>noticeType of a notice whose two trailing bytes are numeric parameters.</summary>
    public const byte NumericParamNotice = 12;

 /// <summary>Blob bytes other than the text: 8 header + 1 type + 2 length + 4 colour + 2 parameters.</summary>
    internal const int FixedBlobBytes = 17;

 /// <summary>
 /// The most characters a notice can carry, so that the blob length (17 + 2·n) fits in the u16 innerSize field.
 /// </summary>
    public const int MaxChars = (ushort.MaxValue - FixedBlobBytes) / 2;

 /// <summary>
 /// Builds the 2668 body. <paramref name="rgb"/> is <c>0xRRGGBB</c>; the client adds the alpha. The text is
 /// truncated to <see cref="MaxChars"/>. <paramref name="paramAt41"/> and <paramref name="paramAt42"/> are read by
 /// the client only when <paramref name="noticeType"/> is <see cref="NumericParamNotice"/>.
 /// </summary>
    public static byte[] Build2668(string text, uint rgb, byte noticeType, byte paramAt41, byte paramAt42)
    {
        var units = Encoding.Unicode.GetBytes(text);
        int nChars = Math.Min(units.Length / 2, MaxChars);
        int blobLen = FixedBlobBytes + nChars * 2;

        using var w = new PacketWriter(4 + blobLen);
        w.WriteUInt32((uint)blobLen);              
        w.WriteUInt16((ushort)blobLen);            
        w.WriteUInt16(0);                          
        w.WriteUInt32(0);                          
        w.WriteByte(noticeType);                   
        w.WriteUInt16((ushort)nChars);             
        w.WriteBytes(units.AsSpan(0, nChars * 2)); 
        w.WriteByte((byte)(rgb >> 16));            
        w.WriteByte((byte)(rgb >> 8));             
        w.WriteByte((byte)rgb);                    
        w.WriteByte(0);                            
        w.WriteByte(paramAt41);
        w.WriteByte(paramAt42);
        return w.ToArray();
    }
}
