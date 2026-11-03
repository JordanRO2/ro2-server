namespace RO2.Companions.Domain;

/// <summary>One vehicle entry of a 2522/2838 list: id, expiry as an OLE date (0 = permanent) and its state byte.</summary>
public readonly record struct VehicleListRecord(uint RidingId, double ExpiryOle, byte State);
