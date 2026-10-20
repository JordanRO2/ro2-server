namespace RO2.Domain.Entities;

/// <summary>A vehicle owned by a character: its RidingInfo id and expiry.</summary>
public sealed class OwnedVehicle
{
 /// <summary>Identifier of the vehicle in the RidingInfo data table.</summary>
    public uint RidingId { get; set; }

 /// <summary>
 /// Expiry as an OLE automation date on the server's local clock; 0 means permanent. Expired vehicles
 /// are kept and reported as expired.
 /// </summary>
    public double ExpiryOle { get; set; }
}
