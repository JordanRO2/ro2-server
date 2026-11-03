namespace RO2.DungeonFinder.Domain;

/// <summary>A finder seat: the stable character plus the opaque session handle that proves it is still the live player.</summary>
public readonly record struct SeatKey(uint CharacterId, uint SessionId);
