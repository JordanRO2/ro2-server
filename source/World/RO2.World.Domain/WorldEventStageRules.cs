namespace RO2.Worlds.Domain;

/// <summary>
/// The live state of one world event as a plain value: its stage, when that stage began, and the offering count of each
/// row of the stage's check group, in index order.
/// </summary>
/// <param name="Id">World event identifier (WorldEvent_Main.World_Event_ID).</param>
/// <param name="Stage">Current stage level.</param>
/// <param name="StageEnteredUtc">When the current stage began (UTC). A stage with a positive duration ends that many minutes later.</param>
/// <param name="Counts">Items offered in this stage, one count per row of the stage's check group. Zero when a stage begins.</param>
/// <param name="UpdatedUtc">When the state last changed (UTC).</param>
public sealed record WorldEventState(uint Id, byte Stage, DateTime StageEnteredUtc, IReadOnlyList<uint> Counts, DateTime UpdatedUtc);

/// <summary>Result of <see cref="WorldEventStageRules.AddOffering"/>.</summary>
/// <param name="Counted">Whether the offering counted toward the stage.</param>
/// <param name="NextStage">The stage the offering completed, or null when it did not complete one.</param>
/// <param name="Counts">The stage's counts after the offering. Equal to the input counts when it did not count.</param>
public readonly record struct WorldEventOfferingResult(bool Counted, byte? NextStage, IReadOnlyList<uint> Counts);

/// <summary>
/// World-event stage rules as pure functions of a <see cref="WorldEventState"/> and the tables. The current time enters as
/// a parameter and nothing is stored or mutated.
/// </summary>
/// <remarks>
/// Each stage completes when every offering row of its check group is met. Each row counts separately and the counts restart at each stage.
/// A stage that has a duration returns to its return stage when the duration elapses.
/// </remarks>
public static class WorldEventStageRules
{
 /// <summary>A new state for an event entering a stage at the given time.</summary>
    public static WorldEventState Enter(uint eventId, byte stage, DateTime nowUtc, WorldEventTables t)
    {
        var row = t.Stage(eventId, stage);
        var rows = row is null ? 0 : t.OfferingRows(row).Count;
        return new WorldEventState(eventId, stage, nowUtc, Enumerable.Repeat(0u, rows).ToArray(), nowUtc);
    }

 /// <summary>
 /// The stored state adjusted to the tables, with one count per offering row of its stage, or null when its stage no longer exists.
 /// </summary>
    public static WorldEventState? Normalize(WorldEventState stored, WorldEventTables t)
    {
        var row = t.Stage(stored.Id, stored.Stage);
        if (row is null) return null;
        var counts = new List<uint>(stored.Counts);
        var rows = t.OfferingRows(row).Count;
        if (counts.Count > rows) counts.RemoveRange(rows, counts.Count - rows);
        while (counts.Count < rows) counts.Add(0);
        return stored with { Counts = counts };
    }

 /// <summary>
 /// Counts an offering of items to an NPC toward the current stage. Does not change <paramref name="state"/>: the
 /// counts after the offering are in the result.
 /// </summary>
 /// <remarks>
 /// The offering counts only when the item and NPC pair is a row of the current stage's first check group.
 /// The next stage is reported once every row in the group has reached its required count.
 /// </remarks>
    public static WorldEventOfferingResult AddOffering(WorldEventState state, WorldEventTables t, uint npcTemplate,
        uint itemTemplate, int count)
    {
        var unchanged = new WorldEventOfferingResult(false, null, state.Counts);
        if (count <= 0) return unchanged;
        var row = t.Stage(state.Id, state.Stage);
        if (row is null) return unchanged;
        var rows = t.OfferingRows(row);
        int i = -1;
        for (int k = 0; k < rows.Count; k++)
            if (rows[k].Type_Data_1 == npcTemplate && rows[k].Type_Data_2 == itemTemplate) { i = k; break; }
        if (i < 0) return unchanged;

        var counts = new List<uint>(state.Counts);
        while (counts.Count < rows.Count) counts.Add(0);
        counts[i] = (uint)Math.Min((ulong)counts[i] + (ulong)count, uint.MaxValue);

        for (int k = 0; k < rows.Count; k++)
            if (counts[k] < rows[k].Type_Data_3) return new WorldEventOfferingResult(true, null, counts);
        return t.Stage(state.Id, row.Next_Stage) is null
            ? new WorldEventOfferingResult(true, null, counts)
            : new WorldEventOfferingResult(true, row.Next_Stage, counts);
    }

 /// <summary>When the current stage times out, or null when it does not.</summary>
    public static DateTime? Deadline(WorldEventState state, WorldEventTables t)
        => t.Stage(state.Id, state.Stage) is { Stage_Duration_Time: > 0 } row
            ? state.StageEnteredUtc.AddMinutes(row.Stage_Duration_Time)
            : null;

 /// <summary>The return stage the event falls back to at the given time, or null while the stage is still running.</summary>
    public static byte? TimedOut(WorldEventState state, WorldEventTables t, DateTime nowUtc)
    {
        if (Deadline(state, t) is not { } deadline || nowUtc < deadline) return null;
        var row = t.Stage(state.Id, state.Stage)!;
        return t.Stage(state.Id, row.Return_Stage) is null ? null : row.Return_Stage;
    }
}
