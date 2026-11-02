namespace RO2.Guilds.Domain;

/// <summary>One line of a guild vault's item history: who moved which item in or out, and when. The guild
/// the line belongs to is the key it was stored under, not a field of the line.</summary>
public sealed class GuildStorageLogEntry
{
 /// <summary>When the move happened (UTC).</summary>
    public DateTime WhenUtc { get; set; }
 /// <summary>Name of the character that made the move.</summary>
    public string ActorName { get; set; } = string.Empty;
 /// <summary>0 = deposit, 1 = withdraw.</summary>
    public byte Kind { get; set; }
 /// <summary>Id of the item moved.</summary>
    public uint ItemId { get; set; }
 /// <summary>How many of the item moved.</summary>
    public int Count { get; set; }
}
