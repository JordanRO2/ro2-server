using System.Buffers.Binary;
using RO2.Network.ProudNet;
using RO2.Network.Throttling;

namespace RO2.Server.Chat;

/// <summary>
/// Per-sender flood limit on community-channel chat: a 23018 line is refused, with
/// 23536 NfyChannelChatError [u16 22] to the sender and no 23528 to anyone, when that sender already had
/// <see cref="MaxLinesPerWindow"/> lines delivered in the last <see cref="WindowMs"/> ms.
///
/// The rule: the limit is K = 4 delivered lines inside a W = 5 s sliding window, per sender across the
/// community channels only (the scope of "channel" chat that 23536 reports on). The refusal frame is
/// 23536 with body <c>16 00</c>; the client maps code 22 to string 8669, "Chat scrolling is too fast."
/// The refused line is never echoed on 23528.
///
/// C-H5: keyed by CHARACTER id, not by session, so a reconnect does not reset the window, and shared by all three
/// send paths (channel 23528, private-room 23529, close-range 23529). K = 4 / W = 5000 ms apply to the channel path;
/// extending the gate to the private-room and close-range paths is defensive hardening. Only the channel path has
/// a refusal frame (23536); the other two drop the line silently.
/// </summary>
public static class ChannelChatFloodGate
{
    /// <summary>Delivered lines a sender may have inside the window; the next one is refused.</summary>
    public const int MaxLinesPerWindow = 4;

    /// <summary>Window length. Sliding-window length in milliseconds.</summary>
    public const long WindowMs = 5000;

    /// <summary>23536 result code the client shows as string 8669, "Chat scrolling is too fast."</summary>
    public const ushort ChatScrollingTooFast = 22;

    private static readonly SlidingWindowRateLimiter<uint> Windows = new(MaxLinesPerWindow, WindowMs);

    /// <summary>
    /// True, and the line is counted, when the character behind <paramref name="session"/> may post another line
    /// now; false when it must be refused. A session with no resolved character id is refused. Refused lines are
    /// not counted, since a refused line reaches nobody.
    /// </summary>
    public static bool TryAdmit(ProudSession session)
        => session.CharacterId is { } characterId && Windows.TryAcquire(characterId);

    /// <summary><see cref="TryAdmit(ProudSession)"/> against an explicit monotonic-clock time, for deterministic tests.</summary>
    public static bool TryAdmit(uint characterId, long nowMs) => Windows.TryAcquire(characterId, nowMs);

    /// <summary>S2C 23536 body for a refused line: [u16 22] (bytes <c>16 00</c>).</summary>
    public static byte[] RefusalBody()
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, ChatScrollingTooFast);
        return body;
    }
}
