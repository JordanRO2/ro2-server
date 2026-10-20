using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the 2730 NfyGuildHandoverMemberBribePoint_ToGuildMaster notification, sent to the guild master when
/// a member leaves or is kicked. The body carries the master's contribution total.
/// </summary>
/// <remarks>
/// Sent by the leave (1432) and kick (1431) handlers, just before the 2571 member-remove notification, and to
/// the master only. The client sets the master's contribution to the value, so it is the new total, not the
/// amount moved. It is sent even when the total is 0.
/// </remarks>
public static class GuildHandoverPackets
{
 /// <summary>The fixed 2730 body length.</summary>
    public const int BodyLen = 8;

 /// <summary>2730 body: <c>[u64 masterTotal]</c>, little-endian. A negative total is sent as 0.</summary>
    public static byte[] BuildNfyHandoverMemberBribePointToMaster(long masterTotal)
    {
        var body = new byte[BodyLen];
        BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)Math.Max(0L, masterTotal));
        return body;
    }
}
