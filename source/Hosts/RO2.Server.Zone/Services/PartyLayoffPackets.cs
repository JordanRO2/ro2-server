using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2367 OnPartyNotifyLayoff: the notice sent to a member the party leader expels (1388 ReqPartyExpulsion).
/// It is the only kick message addressed to the kicked player.
/// </summary>
/// <remarks>
/// <para>
/// Body: the compact prefix <c>[01][08]</c>, then the blob <c>[u16 selfLen = 8][u16 msgTag = 58171][u32 0]</c>.
/// The payload carries no party handle or GID, and the client ignores it.
/// </para>
/// </remarks>
public static class PartyLayoffPackets
{
 /// <summary>msgTag at blob+2.</summary>
    public const ushort MsgTag = 58171;

 /// <summary>
 /// Builds the 10-byte 2367 body <c>[01 08][u16 8][u16 58171][u32 0]</c>. It has no parameters.
 /// </summary>
    public static byte[] BuildPartyNotifyLayoff2367()
    {
        using var pw = new PacketWriter(10);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTag);
            p.WriteUInt32(0);
        });
        return pw.ToArray();
    }
}
