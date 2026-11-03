using Microsoft.Extensions.Logging;

namespace RO2.Dungeons.Application;

/// <summary>Records an instance-dungeon entry that has already been handed off. A storage failure is logged, never thrown, so it cannot block the entry.</summary>
public sealed class RecordDungeonEntryUseCase(IDungeonEntryCountRepository counts, ILogger<RecordDungeonEntryUseCase> logger)
{
    /// <summary>Counts one entry into <paramref name="dungeonId"/>; <paramref name="enterCountLimit"/> is only logged.</summary>
    public async Task RecordAsync(uint characterId, uint dungeonId, string name, int enterCountLimit, CancellationToken ct = default)
    {
        try
        {
            var now = await counts.RecordEntryAsync(characterId, dungeonId, DateTime.UtcNow, ct);
            logger.LogInformation("{Name}: DungeonID {Dun} entries {Used}/{Limit} since the last reset",
                name, dungeonId, now, enterCountLimit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: could not count the entry into DungeonID {Dun}", name, dungeonId);
        }
    }
}
