namespace RO2.Network.Protocol;

/// <summary>
/// Encodings for the server time values sent to the client: Unix seconds (RMI 2811) and microseconds since the Julian epoch (RMI 2669).
/// </summary>
/// <remarks>
/// The client uses two unrelated time representations, so each encoding is documented on its own member.
/// </remarks>
public static class ServerTimeCodec
{
 /// <summary>Seconds from the Julian day epoch (midnight of -4712-01-01) to the Unix epoch: exactly 2,440,588 days.</summary>
 /// <remarks>
 /// Julian day 0 starts at noon, half a day before the epoch used here. Using the noon value shifts every timestamp by 12 hours.
 /// </remarks>
    public const long JulianMidnightToUnixSeconds = 2_440_588L * 86_400L; 

 /// <summary>The same offset in milliseconds.</summary>
    private const long JulianMidnightToUnixMilliseconds = JulianMidnightToUnixSeconds * 1000L;

 /// <summary>
 /// Encodes the body of RMI 2669 (NfyServerTimeToLoginPC): a u64 little-endian count of microseconds since the Julian midnight epoch.
 /// </summary>
 /// <remarks>
 /// The client reads this value with a Boost.DateTime-based reader. An out-of-range value makes that reader throw instead of failing cleanly.
 /// </remarks>
    public static ulong ToClientMicroseconds(DateTimeOffset when)
        => (ulong)((when.ToUnixTimeMilliseconds() + JulianMidnightToUnixMilliseconds) * 1000L);

 /// <summary>Inverse of <see cref="ToClientMicroseconds"/>.</summary>
    public static DateTimeOffset FromClientMicroseconds(ulong micros)
        => DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(micros / 1000UL) - JulianMidnightToUnixMilliseconds);

 /// <summary>
 /// Builds the body of RMI 2811 (NfyServerTime): a u32 little-endian Unix time in seconds, then a u32 zero. 8 bytes in total.
 /// </summary>
 /// <remarks>
 /// The server sends this about every 60 seconds.
 /// </remarks>
    public static byte[] BuildServerTimeBody(DateTimeOffset when)
    {
        var body = new byte[8];
        var secs = (uint)when.ToUnixTimeSeconds();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, secs);
        
        return body;
    }

 /// <summary>Send interval for RMI 2811.</summary>
    public static readonly TimeSpan ServerTimeInterval = TimeSpan.FromSeconds(60);
}
