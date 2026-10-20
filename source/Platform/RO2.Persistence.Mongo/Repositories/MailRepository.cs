using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IMailRepository"/> over the <c>mails</c> collection and the
/// <c>mail_counters</c> sequence doc (<c>_id</c> "mailId"). Stateless apart from its collection handles, so a DI
/// singleton over the process-wide <see cref="MongoDbContext"/>. The two mail indexes (receiver + newest first,
/// unique uid) are ensured once per process, in the constructor, as the store it replaces did.
/// </summary>
public sealed class MailRepository : IMailRepository
{
    private const string MailsCollection = "mails";
    private const string CountersCollection = "mail_counters";

    /// <summary>The <c>_id</c> of the single counter document in <c>mail_counters</c>.</summary>
    private const string CounterId = "mailId";

    /// <summary>Field of the counter document that holds the last issued mail id.</summary>
    private const string CounterSeqField = "seq";

    /// <summary>Most mails returned for one inbox, newest first.</summary>
    private const int InboxLimit = 200;

    /// <summary>High dword shared by every mail uid: the fixed prefix of the 64-bit mail handle the client expects.</summary>
    private const uint MailUidHighDword = 0x0E4B4B8B;

    private readonly IMongoCollection<MailMessage> _mails;
    private readonly IMongoCollection<BsonDocument> _counters;

    private static int _indexesEnsured; // 0 = not yet

    public MailRepository(MongoDbContext context)
    {
        _mails = context.GetCollection<MailMessage>(MailsCollection);
        _counters = context.GetCollection<BsonDocument>(CountersCollection);
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        if (Interlocked.Exchange(ref _indexesEnsured, 1) != 0) return;
        try
        {
            _mails.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<MailMessage>(
                    Builders<MailMessage>.IndexKeys.Ascending(m => m.ReceiverCharId).Descending(m => m.SentAt)),
                new CreateIndexModel<MailMessage>(
                    Builders<MailMessage>.IndexKeys.Ascending(m => m.Uid),
                    new CreateIndexOptions { Unique = true }),
            });
        }
        catch
        {
            // A racing/duplicate index build is harmless; never let index setup take the server down.
            Interlocked.Exchange(ref _indexesEnsured, 0);
        }
    }

    /// <summary>Atomic increasing id from the <c>mail_counters</c> doc (upserts on first use).</summary>
    private async Task<ulong> NextIdAsync(CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", CounterId);
        var update = Builders<BsonDocument>.Update.Inc(CounterSeqField, 1L);
        var opts = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };
        var doc = await _counters.FindOneAndUpdateAsync(filter, update, opts, ct);
        var seq = doc != null && doc.Contains(CounterSeqField) ? doc[CounterSeqField].ToInt64() : 1L;
        return (ulong)seq;
    }

    /// <summary>uid = 0x0E4B4B8B_00000000 | id; the high dword is a fixed prefix of the 64-bit mail handle; the low dword is unique.</summary>
    private static ulong MakeUid(ulong id) => ((ulong)MailUidHighDword << 32) | (uint)id;

    public async Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default)
    {
        var id = await NextIdAsync(ct);
        draft.Id = id;
        draft.Uid = MakeUid(id);
        await _mails.InsertOneAsync(draft, cancellationToken: ct);
        return draft;
    }

    public async Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId, CancellationToken ct = default)
    {
        return await _mails
            .Find(m => m.ReceiverCharId == receiverCharId)
            .SortByDescending(m => m.SentAt)
            .Limit(InboxLimit)
            .ToListAsync(ct);
    }

    public async Task<MailMessage?> GetByUidAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        return await _mails
            .Find(m => m.ReceiverCharId == receiverCharId && m.Uid == uid)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<long> CountUnreadAsync(uint receiverCharId, CancellationToken ct = default)
    {
        return await _mails.CountDocumentsAsync(m => m.ReceiverCharId == receiverCharId && !m.IsRead, cancellationToken: ct);
    }

    public async Task<long> CountLiveInboxAsync(uint receiverCharId, DateTime notBefore, CancellationToken ct = default)
    {
        return await _mails.CountDocumentsAsync(m => m.ReceiverCharId == receiverCharId && m.SentAt >= notBefore, cancellationToken: ct);
    }

    public Task ReplaceAsync(MailMessage mail, CancellationToken ct = default) =>
        _mails.ReplaceOneAsync(m => m.Id == mail.Id, mail, new ReplaceOptions(), ct);

    public async Task<bool> DeleteAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        var res = await _mails.DeleteOneAsync(m => m.ReceiverCharId == receiverCharId && m.Uid == uid, ct);
        return res.DeletedCount > 0;
    }

    public Task<MailMessage?> TryClaimItemAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
        ClaimAsync(BuildClaimItemFilter(receiverCharId, uid), Builders<MailMessage>.Update.Set(m => m.ItemClaimed, true),
            m => m.ItemClaimed = true, ct);

    public Task<MailMessage?> TryClaimGoldAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
        ClaimAsync(BuildClaimGoldFilter(receiverCharId, uid), Builders<MailMessage>.Update.Set(m => m.GoldClaimed, true),
            m => m.GoldClaimed = true, ct);

    /// <summary>Receiver + uid, item attached and not yet claimed.</summary>
    public static FilterDefinition<MailMessage> BuildClaimItemFilter(uint receiverCharId, ulong uid)
    {
        var f = Builders<MailMessage>.Filter;
        return f.Eq(m => m.ReceiverCharId, receiverCharId) & f.Eq(m => m.Uid, uid)
               & f.Eq(m => m.ItemClaimed, false) & f.Ne(m => m.AttachedItemId, 0u);
    }

    /// <summary>Receiver + uid, zeny attached and not yet claimed.</summary>
    public static FilterDefinition<MailMessage> BuildClaimGoldFilter(uint receiverCharId, ulong uid)
    {
        var f = Builders<MailMessage>.Filter;
        return f.Eq(m => m.ReceiverCharId, receiverCharId) & f.Eq(m => m.Uid, uid)
               & f.Eq(m => m.GoldClaimed, false) & f.Gt(m => m.Gold, 0L);
    }

    // The flip runs server-side in one findOneAndUpdate; the document comes back as it was before it, so the
    // flag is set on the copy to give the caller the same post-claim view the Postgres provider returns.
    private async Task<MailMessage?> ClaimAsync(FilterDefinition<MailMessage> filter, UpdateDefinition<MailMessage> update,
        Action<MailMessage> markClaimed, CancellationToken ct)
    {
        var before = await _mails.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<MailMessage> { ReturnDocument = ReturnDocument.Before }, ct);
        if (before is not null) markClaimed(before);
        return before;
    }

    public async Task<bool> DeleteIfSpentAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        var f = Builders<MailMessage>.Filter;
        var filter = f.Eq(m => m.ReceiverCharId, receiverCharId) & f.Eq(m => m.Uid, uid)
                     & (f.Eq(m => m.AttachedItemId, 0u) | f.Eq(m => m.ItemClaimed, true))
                     & (f.Lte(m => m.Gold, 0L) | f.Eq(m => m.GoldClaimed, true));
        var res = await _mails.DeleteOneAsync(filter, ct);
        return res.DeletedCount > 0;
    }
}
