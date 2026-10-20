using RO2.Network.Packets;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2360 (OnPartyNotifyOut): tells the other members of a party that one member is no longer present,
/// either removed from the roster or kept in it and locked.
/// </summary>
/// <remarks>
/// The body carries the party handle, the member GID and a kind byte selecting
/// <see cref="KindRemoved"/> or <see cref="KindLocked"/>.
/// </remarks>
public static class PartyNotifyOutPackets
{
 /// <summary>RMI 2360 (OnPartyNotifyOut) opcode.</summary>
    public const ushort Opcode = ZoneS2COpcodes.OnPartyNotifyOut;

 /// <summary>Message tag of RMI 2360.</summary>
    public const ushort MsgTag = 58164;

 /// <summary>Kind value: the member is removed from the party roster on the receiving clients.</summary>
    public const byte KindRemoved = 0;

 /// <summary>
 /// Kind value: the member stays in the roster as locked (temporarily absent). Receiving clients remove the
 /// member if it does not return before the deadline.
 /// </summary>
    public const byte KindLocked = 1;

 /// <summary>
 /// Builds the 2360 body. <paramref name="partyHandle"/> and <paramref name="memberGid"/> must match the values
 /// the receiving clients got in the roster (2357, 2359, 2361), because the GID is the key the client uses.
 /// </summary>
    public static byte[] BuildPartyNotifyOut2360(uint partyHandle, ulong memberGid, byte kind)
    {
        using var pw = new PacketWriter(32);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTag);   
            p.WriteUInt32(0);        
            p.WriteUInt32(partyHandle); 
            p.WriteUInt64(memberGid);   
            p.WriteByte(kind);          
        });
        return pw.ToArray();
    }
}
