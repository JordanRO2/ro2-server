using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Socials.Domain;

namespace RO2.Socials.Infrastructure.Mongo;

/// <summary>Mongo-backed <see cref="IFriendListRepository"/> over the <c>chat_friend_lists</c> collection.</summary>
public sealed class MongoFriendListRepository : IFriendListRepository
{
    public const string CollectionName = "chat_friend_lists";

    private readonly MongoDbContext _db;

    public MongoFriendListRepository(MongoDbContext db) => _db = db;

    private IMongoCollection<FriendListDocument> Lists()
        => _db.GetCollection<FriendListDocument>(CollectionName);

    public async Task<IReadOnlyList<uint>> LoadAsync(uint ownerId, CancellationToken ct = default)
    {
        var doc = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        return doc is null ? Array.Empty<uint>() : doc.FriendIds;
    }

    /// <summary>
    /// The add is a single size-guarded <c>UpdateOne</c> — <c>$addToSet</c> runs only while the stored
    /// list holds fewer than <see cref="SocialRules.MaxFriends"/> ids ($expr/$size) — so two requests that both
    /// pass the handler's pre-lookup count check cannot both land and push the list past the cap. The guard
    /// counts the raw <see cref="FriendListDocument.FriendIds"/> array, which still holds ids of since-deleted
    /// characters; the handler keeps the live count for the 33 answer, and this is only the concurrency
    /// backstop. A full list that also already holds the friend answers <see cref="AddOutcome.AlreadyFriend"/>,
    /// matched by re-reading the document.
    /// </summary>
    public async Task<AddOutcome> AddAsync(uint ownerId, uint friendId)
    {
        // The driver renders FriendIds.Count < MaxFriends as {"FriendIds.<Max-1>": {$exists: false}}, i.e. the
        // list has no element at the last index, so there is room for one more. $addToSet runs only then.
        var guard = Builders<FriendListDocument>.Filter.And(
            Builders<FriendListDocument>.Filter.Eq(d => d.Id, ownerId),
            Builders<FriendListDocument>.Filter.Where(d => d.FriendIds.Count < SocialRules.MaxFriends));
        var add = Builders<FriendListDocument>.Update.AddToSet(d => d.FriendIds, friendId);

        var result = await Lists().UpdateOneAsync(guard, add);
        if (result.ModifiedCount > 0) return AddOutcome.Added;
        if (result.MatchedCount > 0) return AddOutcome.AlreadyFriend; // had room, $addToSet was a no-op

        // No match: no list document yet, or the list is full. Re-read to tell those apart.
        var doc = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync();
        if (doc is not null)
            return doc.FriendIds.Contains(friendId) ? AddOutcome.AlreadyFriend : AddOutcome.ListFull;

        // Truly absent: create it. The guarded upsert keeps the cap even against a concurrent first-add, and
        // the loser of that create race gets a duplicate _id error, after which the document exists and one
        // conditional retry settles the outcome.
        try
        {
            await Lists().UpdateOneAsync(guard, add, new UpdateOptions { IsUpsert = true });
            return AddOutcome.Added;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var retry = await Lists().UpdateOneAsync(guard, add);
            if (retry.ModifiedCount > 0) return AddOutcome.Added;
            if (retry.MatchedCount > 0) return AddOutcome.AlreadyFriend;
            var after = await Lists().Find(d => d.Id == ownerId).FirstOrDefaultAsync();
            return after is not null && after.FriendIds.Contains(friendId) ? AddOutcome.AlreadyFriend : AddOutcome.ListFull;
        }
    }

    /// <summary>
    /// The filter requires the id to be present, so a delete for an id the owner never listed (a forged or
    /// repeated 23005) matches nothing and writes nothing.
    /// </summary>
    public async Task RemoveAsync(uint ownerId, uint friendId)
        => await Lists().UpdateOneAsync(
            d => d.Id == ownerId && d.FriendIds.Contains(friendId),
            Builders<FriendListDocument>.Update.Pull(d => d.FriendIds, friendId));

    public async Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct = default)
    {
        var list = await Lists().Find(d => d.Id == listOwnerId).FirstOrDefaultAsync(ct);
        return list != null && list.FriendIds.Contains(friendId);
    }

    public async Task<IReadOnlyList<uint>> OwnersListingAsync(uint friendId, CancellationToken ct = default)
        => await Lists()
            .Find(Builders<FriendListDocument>.Filter.AnyEq(d => d.FriendIds, friendId))
            .Project(d => d.Id)
            .ToListAsync(ct);
}
