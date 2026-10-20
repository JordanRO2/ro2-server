namespace RO2.Server.Zone.Services;

/// <summary>
/// Body builders for the web-mall delivery notification.
/// </summary>
/// <remarks>
/// The client reads a fixed six-byte body with no length prefix or message tag, so the RMI body is the six bytes themselves.
/// </remarks>
public static class WebMallNotifyPackets
{
 /// <summary>Body length of the web-mall delivery notification, in bytes.</summary>
    public const int NotifyBytes = 6;

 /// <summary>
 /// Builds the 2638 NfyWebMallItems body: the number of entries waiting in each of the two gift boxes.
 /// </summary>
 /// <remarks>
 /// Layout: a u16 with the entries in box 1, a u16 with the entries in box 2, and a reserved u16 that is always zero.
 /// The counts are entries, not units. The client sends the request with each card-book answer, and the server sends this when a gift arrives.
 /// </remarks>
    public static byte[] Build2638(int purchased, int gifts)
    {
        var body = new byte[NotifyBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)Math.Clamp(purchased, 0, ushort.MaxValue));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)Math.Clamp(gifts, 0, ushort.MaxValue));
        return body;
    }
}
