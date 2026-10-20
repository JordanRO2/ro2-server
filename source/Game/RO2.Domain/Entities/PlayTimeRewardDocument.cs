namespace RO2.Domain.Entities;

/// <summary>
/// Persisted play-time reward progress of one account, shared across its characters and sessions.
/// <see cref="Id"/> is the account id.
/// </summary>
public sealed class PlayTimeRewardDocument
{
    public uint Id { get; set; }

 /// <summary>
 /// Attendance.ID of the reward row in progress. Row ids are used rather than indexes, so data changes
 /// do not move progress onto another reward.
 /// </summary>
    public uint SlotId { get; set; }

 /// <summary>Online seconds accumulated towards the current row.</summary>
    public double AccumulatedSeconds { get; set; }

 /// <summary>
 /// UTC start of the reset period this progress belongs to. Progress from an earlier period is reset
 /// to the first row on the next read.
 /// </summary>
    public DateTime PeriodStartUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
