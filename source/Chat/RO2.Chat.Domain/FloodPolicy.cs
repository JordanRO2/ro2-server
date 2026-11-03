using System.Buffers.Binary;

namespace RO2.Chats.Domain;

/// <summary>
/// The pure numbers of the per-sender community-channel flood limit: K = 4 delivered lines inside a W = 5 s
/// sliding window; a refused line gets 23536 NfyChannelChatError [u16 22]. The live per-character window state
/// stays in the host gate (<c>ChannelChatFloodGate</c>).
/// </summary>
public static class FloodPolicy
{
    /// <summary>Delivered lines a sender may have inside the window; the next one is refused.</summary>
    public const int MaxLinesPerWindow = 4;

    /// <summary>Window length. Sliding-window length in milliseconds.</summary>
    public const long WindowMs = 5000;

    /// <summary>23536 result code the client shows as string 8669, "Chat scrolling is too fast."</summary>
    public const ushort ChatScrollingTooFast = 22;

    /// <summary>S2C 23536 body for a refused line: [u16 22] (bytes <c>16 00</c>).</summary>
    public static byte[] RefusalBody()
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, ChatScrollingTooFast);
        return body;
    }
}
