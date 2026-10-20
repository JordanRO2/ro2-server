using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the vehicle list (2522 OnAllVehicleList), its quick slot (2523 OnVehicleQuickSlot),
/// and the single-vehicle updates (2838 OnVehicleListUpdate and 3023 AnsRidingExtendItem).
/// </summary>
/// <remarks>
/// Wire layout: U32 envelope [u32 len = 2 + 15 * n][u16 n][n x 15-byte record].
/// Record: [+0 u32 RidingInfo id][+4 f64 expiry as an OLE date on the server clock, 0 = permanent][+12 u8 1]
/// [+13 u8 state: 1 permanent, 2 timed, 4 expired][+14 u8 0].
/// The server cuts a character's stored expiry to whole seconds at login. Later grants in the same session keep the stored value.
/// </remarks>
internal static class VehiclePackets
{
    /// <summary>Bytes of one vehicle record.</summary>
    internal const int RecordLength = 15;

    /// <summary>Bytes of the u16 record count that follows the u32 envelope length.</summary>
    internal const int CountFieldLength = 2;

    /// <summary>Bytes of the u32 envelope length that opens the body.</summary>
    internal const int EnvelopeLengthFieldLength = 4;

    /// <summary>Record +12: a constant byte, always 1.</summary>
    internal const byte RecordMarker = 1;

    /// <summary>Record +14: a constant byte, always 0.</summary>
    internal const byte RecordTail = 0;

    /// <summary>Record state of a permanent vehicle (1).</summary>
    internal const byte StatePermanent = 1;

    /// <summary>Record state of a timed vehicle that has not yet expired (2).</summary>
    internal const byte StateTimed = 2;

    /// <summary>Record state of a timed vehicle that has expired (4).</summary>
    internal const byte StateExpired = 4;

    /// <summary>
    /// Record state of a 2838 for a timed vehicle expiring within the next 60 minutes (8).
    /// </summary>
    internal const byte StateExpiringSoon = 8;

    /// <summary>
    /// The server's local wall clock as an OLE automation date.
    /// </summary>
    internal static double NowOle() => ServerWallClock.Now().DateTime.ToOADate();

    /// <summary>Record state for a vehicle with the given expiry at the given time.</summary>
    internal static byte StateOf(double expiryOle, double nowOle)
        => expiryOle == 0 ? StatePermanent : expiryOle > nowOle ? StateTimed : StateExpired;

    /// <summary>
    /// The expiry cut to its whole second. 0 (permanent) stays 0, and a value already on a whole second is returned unchanged.
    /// </summary>
    internal static double WholeSecondExpiry(double expiryOle) => VehicleRules.WholeSecondExpiry(expiryOle);

    /// <summary>
    /// The 2522 body for the given vehicles, sorted by id, with each state worked out at the given time.
    /// An empty list gives the no-vehicle form.
    /// </summary>
    internal static byte[] BuildAllVehicleList2522(IReadOnlyList<OwnedVehicle> vehicles, double nowOle)
        => BuildList(vehicles.OrderBy(v => v.RidingId)
            .Select(v => new VehicleListRecord(v.RidingId, v.ExpiryOle, StateOf(v.ExpiryOle, nowOle)))
            .ToList());

    /// <summary>
    /// The 2838 body for the given records: [u32 len = 2 + 15 * n][u16 n][n x record].
    /// </summary>
    /// <remarks>
    /// The client searches its last 2522 list for the id. An id missing from that list faults the client,
    /// so callers must name only vehicles the session holds.
    /// </remarks>
    internal static byte[] BuildVehicleListUpdate2838(IReadOnlyList<VehicleListRecord> records) => BuildList(records);

    /// <summary>The U32-enveloped record list shared by 2522 and 2838, in the order given.</summary>
    private static byte[] BuildList(IReadOnlyList<VehicleListRecord> records)
    {
        using var pw = new PacketWriter(EnvelopeLengthFieldLength + CountFieldLength + RecordLength * records.Count);
        pw.WriteUInt32((uint)(CountFieldLength + RecordLength * records.Count)); // envelope length: count + records
        pw.WriteUInt16((ushort)records.Count);
        foreach (var r in records)
        {
            pw.WriteUInt32(r.RidingId);   // +0
            pw.WriteDouble(r.ExpiryOle);  // +4
            pw.WriteByte(RecordMarker);   // +12
            pw.WriteByte(r.State);        // +13
            pw.WriteByte(RecordTail);     // +14
        }
        return pw.ToArray();
    }

    /// <summary>
    /// The empty 2523 vehicle quick slot: the list envelope with no record, bytes <c>02 00 00 00 00 00</c>
    /// (u32 length 2, u16 count 0). The server does not store the quick slot, so the empty form is always sent.
    /// </summary>
    internal static byte[] BuildVehicleQuickSlot2523Empty() => BuildList(Array.Empty<VehicleListRecord>());
}

/// <summary>
/// One 15-byte vehicle record: the RidingInfo id, the stored expiry (OLE date, 0 = permanent) and the state at +13.
/// </summary>
internal readonly record struct VehicleListRecord(uint RidingId, double ExpiryOle, byte State);
