using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IChannelColorRepository"/>, persisting per-character
/// community-channel tab colours in the <c>chat_channel_colors</c> collection (one document per owner
/// character). Written by 23019 ReqPublicChatRoomColor and read back into each 23513 AnsPublicChatRoomList
/// record.
/// </summary>
/// <remarks>
/// The colour is server-side state, not a client-local setting: the 23513 tab list carries the player's
/// last chosen colour for every messenger uid, login after login, so the choice outlives the session. A
/// channel the player never recoloured is absent from the document and keeps its default colour in 23513.
/// A DI singleton over the process-wide <see cref="MongoDbContext"/>.
/// </remarks>
public sealed class ChannelColorRepository : IChannelColorRepository
{
    public const string CollectionName = "chat_channel_colors";

    /// <summary>Attempts at set-then-push before giving up when a racing writer keeps adding the same channel.</summary>
    private const int MaxSetAttempts = 3;

    /// <summary>Mongo positional path to the colour of the <c>Colors</c> element matched by the filter.</summary>
    private const string MatchedColorArgbPath = "Colors.$.Argb";

    /// <summary>One channel's colour: the 23513/23019 channel index (12, 13, 14) and its ARGB.</summary>
    private sealed class ChannelColor
    {
        public ushort Index { get; set; }
        public uint Argb { get; set; }
    }

    /// <summary>One owner's colours. <c>Id</c> is the owner's CharacterId (the messenger uid).</summary>
    [BsonIgnoreExtraElements]
    private sealed class ChannelColorDocument
    {
        [BsonId]
        public uint Id { get; set; }
        public List<ChannelColor> Colors { get; set; } = new();
    }

    private readonly MongoDbContext _db;

    public ChannelColorRepository(MongoDbContext db) => _db = db;

    private IMongoCollection<ChannelColorDocument> Docs()
        => _db.GetCollection<ChannelColorDocument>(CollectionName);

    public async Task SetColorAsync(uint ownerId, ushort channelIndex, uint argb, CancellationToken ct = default)
    {
        // Two single-document atomic ops instead of read-modify-replace, so concurrent sets of different
        // channels for one owner never overwrite each other: (1) set the matching element in place; (2) if
        // the channel is absent, push it (upserting the owner doc). A racing push of the same channel makes
        // (2) hit a duplicate _id; retry from (1), which then finds the element.
        var owner = Builders<ChannelColorDocument>.Filter.Eq(d => d.Id, ownerId);
        var hasChannel = Builders<ChannelColorDocument>.Filter.ElemMatch(d => d.Colors, c => c.Index == channelIndex);
        for (var attempt = 0; attempt < MaxSetAttempts; attempt++)
        {
            var set = await Docs().UpdateOneAsync(owner & hasChannel,
                Builders<ChannelColorDocument>.Update.Set(MatchedColorArgbPath, argb), cancellationToken: ct);
            if (set.MatchedCount > 0) return;
            try
            {
                await Docs().UpdateOneAsync(owner & !hasChannel,
                    Builders<ChannelColorDocument>.Update.Push(d => d.Colors, new ChannelColor { Index = channelIndex, Argb = argb }),
                    new UpdateOptions { IsUpsert = true }, ct);
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Element appeared between (1) and (2): loop and set it.
            }
        }
    }

    public async Task<IReadOnlyDictionary<ushort, uint>> GetColorsAsync(uint ownerId, CancellationToken ct = default)
    {
        var doc = await Docs().Find(d => d.Id == ownerId).FirstOrDefaultAsync(ct);
        var map = new Dictionary<ushort, uint>();
        if (doc is null) return map;
        foreach (var c in doc.Colors) map[c.Index] = c.Argb;
        return map;
    }
}
