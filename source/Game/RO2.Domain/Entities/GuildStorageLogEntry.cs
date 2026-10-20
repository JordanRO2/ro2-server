namespace RO2.Domain.Entities;

/// <summary>One line of a guild vault's item history: who moved which item in or out, and when. The guild
/// the line belongs to is the key it was stored under, not a field of the line.</summary>
public sealed class GuildStorageLogEntry
{
    public DateTime WhenUtc { get; set; }
    public string ActorName { get; set; } = string.Empty;
 /// <summary>0 = deposit, 1 = withdraw.</summary>
    public byte Kind { get; set; }
    public uint ItemId { get; set; }
    public int Count { get; set; }
}
