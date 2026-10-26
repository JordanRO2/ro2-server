using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// World-entry wire builders split out of <see cref="WorldEntryService"/> (B5 Phase 0, S1a): byte-for-byte frame
/// construction with no state. Part of the World context's host wire layer — stays host behind the wire boundary.
/// </summary>
internal static class WorldEntryPackets
{
 /// <summary>Fixed first byte of the map-load body.</summary>
    private const byte MapLoadLeadByte = 0x01;
 /// <summary>Fixed u16 at offset 1 of the map-load body.</summary>
    private const ushort MapLoadMarkerA = 0x2B2B;
 /// <summary>Fixed u16 at offset 4 of the map-load body.</summary>
    private const ushort MapLoadMarkerB = 0xE294;
 /// <summary>Last byte of the map-load body (offset 44). This value is known to spawn the client; the byte is not otherwise identified.</summary>
    private const byte MapLoadTrailerByte = 0xAB;

 /// <summary>Length of the 2311 map-load body (45 bytes).</summary>
    internal const int MapLoad2311Length = 45;

 /// <summary>
 /// Builds the 45-byte map-load body that puts the client on a map at a position.
 /// </summary>
 /// <remarks>
 /// Layout: a fixed header (offsets 0 to 5), the actor handle at offset 6, the map id as a u32 at offset 10, 12 zero bytes, the X, Y and Z position as three floats at offset 26, two zero bytes, the actor handle again at offset 40, and a trailer byte at offset 44.
 /// </remarks>
    internal static byte[] BuildMapLoad2311(uint actorHandle, uint mapId, float x, float y, float z)
    {
        using var tw = new PacketWriter();
        tw.WriteByte(MapLoadLeadByte);  // [0] fixed
        tw.WriteUInt16(MapLoadMarkerA);  // [1] fixed
        tw.WriteByte(0x00);  // [3] fixed zero
        tw.WriteUInt16(MapLoadMarkerB);  // [4] fixed
        tw.WriteUInt32(actorHandle);  // [6] actor handle
        tw.WriteUInt32(mapId);  // [10] map id

        tw.WriteZeros(12);  // [14] zeros
        tw.WriteSingle(x);  // [26] position X
        tw.WriteSingle(y);  // [30] position Y
        tw.WriteSingle(z);  // [34] position Z
        tw.WriteUInt16(0);  // [38] zeros
        tw.WriteUInt32(actorHandle);  // [40] actor handle again
        tw.WriteByte(MapLoadTrailerByte);  // [44] trailer byte
        return tw.ToArray();
    }
}
