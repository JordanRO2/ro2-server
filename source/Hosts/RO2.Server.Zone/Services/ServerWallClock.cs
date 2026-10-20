namespace RO2.Server.Zone.Services;

/// <summary>
/// The instant RO2's server-time opcodes carry: the server's LOCAL wall clock, written as if it were
/// UTC.
/// </summary>
/// <remarks>
/// Both 2669 NfyServerTimeToLoginPC and 2811 NfyServerTime encode a zone-less clock reading (a Boost
/// ptime for 2669, a Unix-style second count for 2811), so what the client shows is whatever wall clock
/// the server wrote. <see cref="RO2.Network.Protocol.ServerTimeCodec"/> stays the pure encoding; this
/// chooses the instant.
/// </remarks>
public static class ServerWallClock
{
 /// <summary>Local wall-clock reading as a zero-offset instant, ready for ServerTimeCodec.</summary>
    public static DateTimeOffset Now() => new(DateTimeOffset.Now.DateTime, TimeSpan.Zero);
}
