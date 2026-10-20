using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IKeyConfigRepository"/>, storing key bindings in the
/// <c>character_keyconfig</c> collection (one document per character, keyed by character id).
/// </summary>
/// <remarks>
/// A mode-0 update with no records only clears the flag and keeps the stored records; a full replace could never
/// produce that state. <see cref="ApplyUpdateAsync"/> does this as one atomic upsert, so concurrent saves cannot
/// interleave.
/// </remarks>
public sealed class KeyConfigRepository : IKeyConfigRepository
{
    private readonly IMongoCollection<KeyConfigDocument> _configs;

    public KeyConfigRepository(MongoDbContext context)
    {
        _configs = context.GetCollection<KeyConfigDocument>("character_keyconfig");
    }

    public async Task<KeyConfigDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        => await _configs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);

    public Task SaveAsync(KeyConfigDocument doc, CancellationToken ct = default) =>
        _configs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);

 /// <summary>True when the update only clears the flag: mode 0 with no records. The stored records are kept.</summary>
    public static bool IsFlagOnlyUpdate(ushort mode, int entryBytes) => mode == 0 && entryBytes == 0;

    public Task ApplyUpdateAsync(uint characterId, ushort mode, byte[] entries, DateTime updatedUtc, CancellationToken ct = default)
    {
        if (IsFlagOnlyUpdate(mode, entries.Length))
            return _configs.UpdateOneAsync(d => d.Id == characterId,
                Builders<KeyConfigDocument>.Update
                    .Set(d => d.Mode, mode)
                    .Set(d => d.UpdatedUtc, updatedUtc)
                    .SetOnInsert(d => d.Entries, Array.Empty<byte>()),
                new UpdateOptions { IsUpsert = true }, ct);

        return SaveAsync(new KeyConfigDocument
        {
            Id = characterId,
            Mode = mode,
            Entries = entries,
            UpdatedUtc = updatedUtc,
        }, ct);
    }
}
