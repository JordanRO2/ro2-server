using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for RMI 3010 AnsTeleportSavePoint, the refusal sent when a 1677 escape request is still inside its cooldown.
/// </summary>
/// <remarks>
/// Fixed 18-byte body: [u16 24384][f64 remaining time in days][i32 status 0][u32 0].
/// The client shows the remaining minutes as a system notice.
/// </remarks>
internal static class TeleportSavePointPackets
{
 /// <summary>Constant u16 at body offset 0. The client does not read it.</summary>
    internal const ushort RetailLeadWord = 24384;

 /// <summary>COleDateTimeSpan status field. Always 0.</summary>
    internal const int SpanStatusValid = 0;

 /// <summary>
 /// Builds the 3010 body from the remaining time in seconds.
 /// </summary>
    internal static byte[] BuildAnsTeleportSavePoint3010(long remainingSeconds)
    {
        var body = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(body, RetailLeadWord);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(2), Math.Max(0, remainingSeconds) / 86400.0);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(10), SpanStatusValid);
        
        return body;
    }
}
