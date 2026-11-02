namespace RO2.Guilds.Domain;

/// <summary>One line of a guild vault's funds ("Cash History") history: who deposited or withdrew how much
/// zeny, and when. Written by the 1487 / 1488 handlers after the move has really happened.</summary>
public sealed class GuildFundsLogEntry
{
 /// <summary>When the move happened (UTC).</summary>
    public DateTime WhenUtc { get; set; }
 /// <summary>Name of the character that made the move.</summary>
    public string ActorName { get; set; } = string.Empty;
 /// <summary>0 = deposit, 1 = withdraw: the client's funds formatter uses the same two values.</summary>
    public byte Kind { get; set; }
 /// <summary>Zeny amount moved.</summary>
    public long Amount { get; set; }
}
