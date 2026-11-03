namespace RO2.Dungeons.Domain;

/// <summary>
/// The owner of an instance dungeon: the party the character is in when it enters, otherwise the character itself.
/// </summary>
/// <remarks>
/// Instances are keyed by the party context at the time of entry, so a party's instances stay with the party
/// across channel hand-offs.
/// </remarks>
public readonly record struct DungeonInstanceOwner(bool IsParty, uint Id)
{
 /// <summary>The owner an entry or a reset by <paramref name="characterId"/> resolves to.</summary>
    public static DungeonInstanceOwner For(uint? partyId, uint characterId)
        => partyId is { } pid ? new DungeonInstanceOwner(true, pid) : new DungeonInstanceOwner(false, characterId);

    public override string ToString() => IsParty ? $"party {Id}" : $"character {Id}";
}
