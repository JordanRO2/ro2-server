using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Socials.Domain;

namespace RO2.Socials.Infrastructure.Mongo;

/// <summary>Mongo-backed <see cref="IBlockListRepository"/> over the <c>chat_block_lists</c> collection.</summary>
public sealed class MongoBlockListRepository : IBlockListRepository
{
    /// <summary>The Mongo collection holding one block-list document per owner character.</summary>
    public const string CollectionName = "chat_block_lists";

    private readonly IMongoCollection<BlockListDocument> _lists;

    public MongoBlockListRepository(MongoDbContext context)
        => _lists = context.GetCollection<BlockListDocument>(CollectionName);

    public async Task<IReadOnlyList<BlockListEntry>> GetStoredEntriesAsync(uint ownerId, CancellationToken ct = default)
    {
        var doc = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        return doc is null ? Array.Empty<BlockListEntry>() : doc.Entries;
    }

    /// <summary>
    /// C-H4: a single conditional <c>UpdateOne</c> replaces the former read-modify-ReplaceOne, so two
    /// concurrent blocks cannot both read the old document and lose one of the writes. The <c>$push</c> runs
    /// only while the character is not already present (ElemMatch) and the list is below the cap ($size), so
    /// it never duplicates an entry or grows past the bound. The entries are <c>{Uid, Name}</c> objects, so a
    /// plain <c>$addToSet</c> could not dedup by id alone (a renamed entry would differ); the not-present
    /// guard gives the same idempotence.
    /// </summary>
    public async Task<BlockAddOutcome> AddAsync(uint ownerId, uint characterId, string name, CancellationToken ct = default)
    {
        ulong storedId = characterId;
        // $push runs only while the character is not already present (ElemMatch) and the list has room
        // (Entries.Count < MaxBlocked renders as {"Entries.<Max-1>": {$exists: false}}).
        var guard = Builders<BlockListDocument>.Filter.And(
            Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
            Builders<BlockListDocument>.Filter.Not(
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId)),
            Builders<BlockListDocument>.Filter.Where(d => d.Entries.Count < SocialRules.MaxBlocked));
        var push = Builders<BlockListDocument>.Update.Push(d => d.Entries, new BlockListEntry { Uid = storedId, Name = name });

        var result = await _lists.UpdateOneAsync(guard, push, cancellationToken: ct);
        if (result.ModifiedCount > 0) return BlockAddOutcome.Added;

        // No match: no document yet, the character is already blocked, or the list is full. Re-read to tell
        // them apart - the first two have protocol answers; the cap has none.
        var doc = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        if (doc is not null)
            return doc.Entries.Any(e => CharacterGid.CharacterIdOf(e.Uid) == characterId)
                ? BlockAddOutcome.AlreadyRegistered
                : BlockAddOutcome.ListFull;

        // Truly absent: create it. The guarded upsert holds the cap even against a concurrent first-block, and
        // the loser of that create race gets a duplicate _id error, after which the document exists and one
        // conditional retry settles the outcome.
        try
        {
            await _lists.UpdateOneAsync(guard, push, new UpdateOptions { IsUpsert = true }, ct);
            return BlockAddOutcome.Added;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var retry = await _lists.UpdateOneAsync(guard, push, cancellationToken: ct);
            if (retry.ModifiedCount > 0) return BlockAddOutcome.Added;
            var after = await _lists.Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
            return after is not null && after.Entries.Any(e => CharacterGid.CharacterIdOf(e.Uid) == characterId)
                ? BlockAddOutcome.AlreadyRegistered
                : BlockAddOutcome.ListFull;
        }
    }

    public Task<bool> BlocksAsync(uint ownerId, uint characterId, CancellationToken ct = default)
    {
        ulong storedId = characterId;
        return _lists.Find(Builders<BlockListDocument>.Filter.And(
                Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId)))
            .AnyAsync(ct);
    }

    public Task RemoveAsync(uint ownerId, ulong uid, CancellationToken ct = default)
    {
        ulong characterId = CharacterGid.CharacterIdOf(uid);
        // Pull only when the entry is present, so a forged or repeated 23007 for an id the owner never
        // blocked matches nothing and writes nothing. The handler answers 0 either way.
        return _lists.UpdateOneAsync(
            Builders<BlockListDocument>.Filter.And(
                Builders<BlockListDocument>.Filter.Eq(d => d.Id, ownerId),
                Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == characterId)),
            Builders<BlockListDocument>.Update.PullFilter(d => d.Entries, e => e.Uid == characterId),
            cancellationToken: ct);
    }

    public async Task<IReadOnlySet<uint>> OwnersBlockingAsync(
        IReadOnlyCollection<uint> ownerIds, uint characterId, CancellationToken ct = default)
    {
        if (ownerIds.Count == 0) return new HashSet<uint>();

        ulong storedId = characterId;
        var filter = Builders<BlockListDocument>.Filter.And(
            Builders<BlockListDocument>.Filter.In(d => d.Id, ownerIds),
            Builders<BlockListDocument>.Filter.ElemMatch(d => d.Entries, e => e.Uid == storedId));
        var blockers = await _lists.Find(filter).Project(d => d.Id).ToListAsync(ct);
        return blockers.ToHashSet();
    }
}
