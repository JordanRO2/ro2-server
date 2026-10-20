using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for a character's monster-collection progress (summon level + DNA exp per
/// collection). Raw document I/O only: errors propagate, and the Zone wrapper owns the log-and-swallow
/// policy. Updates are a whole-document read-modify-write, so two concurrent DNA feeds of one character can
/// lose an increment (known, preserved).
/// </summary>
public interface IMonsterCollectionProgressRepository
{
 /// <summary>The stored document for <paramref name="characterId"/>, or null when none exists.</summary>
    Task<MonsterCollectionProgressDocument?> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Upserts the whole document (ReplaceOne, IsUpsert).</summary>
    Task SaveAsync(MonsterCollectionProgressDocument doc, CancellationToken ct = default);
}
