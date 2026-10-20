using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for the talent-point notify (RMI 2542 OnGetTalentPoint), sent when a character gains talent points.
/// Uses the compact-length ByteArray envelope, which <see cref="PacketWriter.WriteCompactBABody"/> keeps valid.
/// </summary>
public static class TalentNotifyPackets
{
    
    private const ushort TagOnGetTalentPoint = 58381; 

 /// <summary>
 /// 2542 OnGetTalentPoint. Body 11 bytes: [u16 innerSize 9][u16 msgTag][u32 actorHandle][u8 talentPoints].
 /// <paramref name="talentPoints"/> is the character's running talent-point total.
 /// </summary>
    public static byte[] BuildOnGetTalentPoint(uint actorHandle, byte talentPoints)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnGetTalentPoint);
            p.WriteUInt32(actorHandle);
            p.WriteByte(talentPoints);
        });
        return pw.ToArray();
    }
}
