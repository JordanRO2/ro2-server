using RO2.Dungeons.Domain;
using System.Globalization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Dungeons.Application;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Dungeons.Infrastructure.Mongo;

/// <summary>
/// A character's instance-dungeon entries in the current daily window: one count per DungeonInfo DungeonID.
/// <see cref="Id"/> maps to Mongo <c>_id</c> and is the character id. The counts belong to
/// <see cref="WindowStartUtc"/> and read as zero once the window has passed (reset lazily, no timer).
/// </summary>
[BsonIgnoreExtraElements]
internal sealed class DungeonEntryCountDocument
{
    public uint Id { get; set; }

 /// <summary>The start of the window the counts belong to
 /// (<see cref="DungeonEntryWindow.WindowStart"/>).</summary>
    public DateTime WindowStartUtc { get; set; }

 /// <summary>Entries per DungeonID, keyed by the id in decimal (Mongo document keys are strings).</summary>
    public Dictionary<string, int> Counts { get; set; } = new();

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// MongoDB implementation of <see cref="IDungeonEntryCountRepository"/>, storing
/// <see cref="DungeonEntryCountDocument"/> in the <c>character_dungeon_entries</c> collection. Stateless over
/// the process-wide <see cref="MongoDbContext"/>, so the Zone host registers it as a singleton.
/// </summary>
/// <remarks>
/// The daily reset instant is 10:00 UTC, a measured constant (no table holds it); the counts of an earlier
/// window read as zero. A character's 1521 / 1531 / 1699 are handled one at a time on its session, so the
/// read-modify-write in <see cref="RecordEntryAsync"/> cannot interleave with another write of the same
/// document.
/// </remarks>
public sealed class DungeonEntryCountRepository : IDungeonEntryCountRepository
{
    private const string CollectionName = "character_dungeon_entries";

    private readonly IMongoCollection<DungeonEntryCountDocument> _docs;

    public DungeonEntryCountRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<DungeonEntryCountDocument>(CollectionName);
    }

 /// <summary>The count <paramref name="doc"/> holds for <paramref name="dungeonId"/> in the window of
 /// <paramref name="utcNow"/>: zero for no document, a document of an earlier window, or no entry yet.</summary>
    internal static int UsedIn(DungeonEntryCountDocument? doc, uint dungeonId, DateTime utcNow)
    {
        if (doc is null || doc.WindowStartUtc != DungeonEntryWindow.WindowStart(utcNow))
            return 0;
        return doc.Counts.GetValueOrDefault(Key(dungeonId));
    }

    public async Task<int> GetUsedAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return UsedIn(doc, dungeonId, utcNow);
    }

    public async Task<int> RecordEntryAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default)
    {
        var window = DungeonEntryWindow.WindowStart(utcNow);
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        if (doc is null || doc.WindowStartUtc != window)
            doc = new DungeonEntryCountDocument { Id = characterId, WindowStartUtc = window };
        var key = Key(dungeonId);
        var count = doc.Counts.GetValueOrDefault(key) + 1;
        doc.Counts[key] = count;
        doc.UpdatedUtc = DateTime.UtcNow;
        await _docs.ReplaceOneAsync(d => d.Id == characterId, doc, new ReplaceOptions { IsUpsert = true }, ct);
        return count;
    }

    private static string Key(uint dungeonId) => dungeonId.ToString(CultureInfo.InvariantCulture);
}
