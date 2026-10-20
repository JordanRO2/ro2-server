using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Mongo.Persistence;

/// <summary>
/// MongoDB implementation of <see cref="IChannelPresenceStore"/>: the channel each player is on, shared by
/// the Login, Zone and Chat servers through the database. Login writes the account's selected handle (12005),
/// the Zone overwrites it on a move (1421) and turns it into the character's label at the 1417 that follows
/// every world entry, and the Chat server reads the labels when it builds a 23505. A DI singleton over the
/// process-wide <see cref="MongoDbContext"/>; registered once in <c>AddPersistence</c> so all three hosts
/// share the same contract.
/// </summary>
/// <remarks>
/// A label is only read for a friend online on the Chat server, and the way back online passes a 1417 that
/// rewrites it, so a label left behind by a logout is not shown. The 23505 location is the friend's CURRENT
/// channel, named the way the channel lists name its handle, so a channel move after the messenger login
/// shows up too.
/// </remarks>
public sealed class ChannelPresenceStore : IChannelPresenceStore
{
    private const string AccountCollectionName = "channel_presence_accounts";
    private const string CharacterCollectionName = "channel_presence_characters";

 /// <summary>One account's selected channel. <c>Id</c> is the account id.</summary>
    [BsonIgnoreExtraElements]
    private sealed class AccountChannelDocument
    {
        [BsonId] public uint Id { get; set; }
        public uint ChannelHandle { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

 /// <summary>One character's channel label. <c>Id</c> is the character id.</summary>
    [BsonIgnoreExtraElements]
    private sealed class CharacterLocationDocument
    {
        [BsonId] public uint Id { get; set; }
        public uint ChannelHandle { get; set; }
        public string Location { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly MongoDbContext _db;

    public ChannelPresenceStore(MongoDbContext db) => _db = db;

    private IMongoCollection<AccountChannelDocument> Accounts()
        => _db.GetCollection<AccountChannelDocument>(AccountCollectionName);

    private IMongoCollection<CharacterLocationDocument> Characters()
        => _db.GetCollection<CharacterLocationDocument>(CharacterCollectionName);

 /// <summary>
 /// The label a channel list gives regular channel <paramref name="channel"/>: 'Channel N', what both our
 /// lists write and what retail's 12503 names its handles.
 /// </summary>
    public static string ChannelLabel(int channel) => $"Channel {channel}";

 /// <summary>
 /// The label of the channel whose handle is <paramref name="handle"/> among the
 /// <paramref name="channelCount"/> regular channels a list advertises, where
 /// <paramref name="handleOf"/>(N) is that list's handle of channel N; empty when the list has no such
 /// handle.
 /// </summary>
    public static string LabelOf(uint handle, int channelCount, Func<int, uint> handleOf)
    {
        for (int ch = 1; ch <= channelCount; ch++)
            if (handleOf(ch) == handle)
                return ChannelLabel(ch);
        return "";
    }

    public Task SetAccountChannelAsync(uint accountId, uint channelHandle, CancellationToken ct = default)
        => Accounts().ReplaceOneAsync(d => d.Id == accountId,
            new AccountChannelDocument { Id = accountId, ChannelHandle = channelHandle, UpdatedUtc = DateTime.UtcNow },
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<uint?> GetAccountChannelAsync(uint accountId, CancellationToken ct = default)
    {
        var doc = await Accounts().Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        return doc?.ChannelHandle;
    }

    public Task SetCharacterLocationAsync(uint characterId, uint channelHandle, string location, CancellationToken ct = default)
        => Characters().ReplaceOneAsync(d => d.Id == characterId,
            new CharacterLocationDocument
            {
                Id = characterId,
                ChannelHandle = channelHandle,
                Location = location,
                UpdatedUtc = DateTime.UtcNow,
            },
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyDictionary<uint, string>> GetLocationsAsync(IReadOnlyCollection<uint> characterIds, CancellationToken ct = default)
    {
        if (characterIds.Count == 0)
            return new Dictionary<uint, string>();
        var docs = await Characters()
            .Find(Builders<CharacterLocationDocument>.Filter.In(d => d.Id, characterIds))
            .ToListAsync(ct);
        var byId = new Dictionary<uint, string>(docs.Count);
        foreach (var d in docs)
            if (!string.IsNullOrEmpty(d.Location))
                byId[d.Id] = d.Location;
        return byId;
    }
}
