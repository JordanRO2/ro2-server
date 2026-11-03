namespace RO2.DungeonFinder.Domain;

/// <summary>A finder seat: the stable character plus the session and the player lifetime (Incarnation) that prove it is still the live player.</summary>
public readonly record struct SeatKey(uint CharacterId, uint SessionId, long Incarnation);
