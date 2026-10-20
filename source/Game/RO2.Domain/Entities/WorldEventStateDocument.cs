namespace RO2.Domain.Entities;

/// <summary>
/// Live state of one world event: its stage, when that stage began, and the offering count of each row
/// of the stage's check group. Server-wide, not per character. Persisted, so a restart does not reset
/// the event or forget what was offered.
/// </summary>
public sealed class WorldEventStateDocument
{
 /// <summary>World event identifier (WorldEvent_Main.World_Event_ID); one document per event.</summary>
    public uint Id { get; set; }

 /// <summary>Current stage level.</summary>
    public byte Stage { get; set; }

 /// <summary>When the current stage began (UTC). A stage with a positive duration ends that many minutes later.</summary>
    public DateTime StageEnteredUtc { get; set; }

 /// <summary>
 /// Items offered in this stage, one count per row of the stage's check group, in index order.
 /// Reset to zero when a stage begins.
 /// </summary>
    public List<uint> Counts { get; set; } = new();

    public DateTime UpdatedUtc { get; set; }

 /// <summary>Returns a deep copy, so a caller can change it without touching the published state.</summary>
    public WorldEventStateDocument Clone() => new()
    {
        Id = Id,
        Stage = Stage,
        StageEnteredUtc = StageEnteredUtc,
        Counts = new List<uint>(Counts),
        UpdatedUtc = UpdatedUtc,
    };
}
