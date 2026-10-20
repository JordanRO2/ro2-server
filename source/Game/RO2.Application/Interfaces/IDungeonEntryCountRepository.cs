namespace RO2.Application.Interfaces;

/// <summary>
/// A character's instance-dungeon entries in the current daily window: one count per DungeonInfo DungeonID,
/// the 'used' that 3035 AnsDungeonEnteranceInfo reports and 1521 / 1531 hold against DungeonInfo.Enter_Count.
/// The counts belong to a daily window and read as zero once it has passed (reset lazily, no timer).
/// </summary>
public interface IDungeonEntryCountRepository
{
 /// <summary>The character's entries into <paramref name="dungeonId"/> since the last reset.</summary>
    Task<int> GetUsedAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default);

 /// <summary>Counts one entry of the character into <paramref name="dungeonId"/>, starting a fresh window
 /// when the stored one has passed, and returns the new count.</summary>
    Task<int> RecordEntryAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default);
}
