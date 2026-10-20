namespace RO2.Domain.Entities;

/// <summary>One registered card: its id and the per-character state byte that 2587 carries at record+4.</summary>
public sealed class CardRegistryEntry
{
    public uint CardId { get; set; }

 /// <summary>The record+4 state: 2 = registered and not yet looked at, 1 = seen (the Zone host names them
 /// CardRegistryStore.StateNew and CardRegistryStore.StateSeen).</summary>
    public byte State { get; set; }
}

/// <summary>One character's card registry. <see cref="Id"/> maps to Mongo <c>_id</c> = the character id.</summary>
public sealed class CardRegistryDocument
{
    public uint Id { get; set; }
    public List<CardRegistryEntry> Cards { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
