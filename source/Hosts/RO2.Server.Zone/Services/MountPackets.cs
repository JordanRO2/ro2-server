using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the riding-pet mount notifications. Both messages use the compact-length byte array envelope, so
/// each body starts with a valid length marker.
/// </summary>
/// <remarks>
/// Body layout: a compact length of the self length, then a u16 self length, a u16 message tag and the fields. The self
/// length is 2 plus the payload length, and the payload is the message tag plus the fields.
/// </remarks>
public static class MountPackets
{
 /// <summary>Message tag of the 2712 notification.</summary>
    private const ushort TagNfyDropRidingPet = 58445; 

 /// <summary>
 /// Builds the 2712 NfyDropRidingPet notification, sent when a player dismounts.
 /// </summary>
 /// <remarks>
 /// Fields: a zero u32, the rider's actor handle, the riding skill id, and the vehicle's data id.
 /// </remarks>
    public static byte[] BuildNfyDropRidingPet(uint riderHandle, uint skillId, uint vehicleId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfyDropRidingPet);
            p.WriteUInt32(0);
            p.WriteUInt32(riderHandle);
            p.WriteUInt32(skillId);
            p.WriteUInt32(vehicleId);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2720 NfyRideRidingPet notification, sent when a player mounts. Delegates to
 /// <see cref="ActorStatePackets.BuildNfyRideRidingPet"/>.
 /// </summary>
    public static byte[] BuildNfyRideRidingPet(uint skillId, uint riderHandle, uint vehicleId)
        => ActorStatePackets.BuildNfyRideRidingPet(skillId, riderHandle, vehicleId);
}
