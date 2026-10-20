namespace RO2.Domain.Entities;

/// <summary>One line of a guild vault's funds ("Cash History") history: who deposited or withdrew how much
/// zeny, and when. Written by the 1487 / 1488 handlers after the move has really happened.</summary>
public sealed class GuildFundsLogEntry
{
    public DateTime WhenUtc { get; set; }
    public string ActorName { get; set; } = string.Empty;
 /// <summary>0 = deposit, 1 = withdraw: the client's funds formatter uses the same two values.</summary>
    public byte Kind { get; set; }
    public long Amount { get; set; }
}
