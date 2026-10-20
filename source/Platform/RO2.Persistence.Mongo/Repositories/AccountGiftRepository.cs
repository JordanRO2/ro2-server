using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IAccountGiftRepository"/> over the <c>account_gifts</c> collection (one
/// document per login account, <c>_id</c> = the account id). Stateless apart from its collection handle, so a DI
/// singleton over the process-wide <see cref="MongoDbContext"/>.
/// </summary>
public sealed class AccountGiftRepository : IAccountGiftRepository
{
    private const string CollectionName = "account_gifts";

    /// <summary>
    /// One account's stored box and its daily attendance. <see cref="Id"/> maps to Mongo <c>_id</c> = the account id.
    /// A box written before attendance existed has neither attendance field and reads as never attended (0, 0, 0).
    /// </summary>
    [BsonIgnoreExtraElements]
    private sealed class AccountGiftDocument
    {
        public uint Id { get; set; }
        public List<AccountGift> Gifts { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Days attended in the current attendance cycle (1..28, the count 3027 carries); 0 before the first.</summary>
        public int AttendedDays { get; set; }

        /// <summary>The last attended server day, as <see cref="DateOnly.DayNumber"/> on the server clock; 0 before the first.</summary>
        public int LastAttendedDay { get; set; }

        /// <summary>
        /// The attendance streak of the current cycle: 0 while every server day since the cycle's day 1 was attended,
        /// otherwise the count attended before the first missed day. Set in the same write that records the day
        /// (<see cref="TryRecordAttendanceAsync"/>); a box written before this field existed reads it as 0.
        /// </summary>
        public int AttendanceStreakBrokenAfter { get; set; }
    }

    private readonly IMongoCollection<AccountGiftDocument> _docs;

    public AccountGiftRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<AccountGiftDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<AccountGift>> GetLiveAsync(uint accountId, DateTime nowLocal, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        if (doc is null) return new List<AccountGift>();
        if (doc.Gifts.Any(g => !AccountGiftRules.IsLive(g, nowLocal)))
        {
            var cutoffUtc = OldestLiveLocal(nowLocal).ToUniversalTime();
            await _docs.UpdateOneAsync(d => d.Id == accountId,
                Builders<AccountGiftDocument>.Update.PullFilter(d => d.Gifts, g => g.ReceivedUtc < cutoffUtc), cancellationToken: ct);
        }
        return AccountGiftRules.NewestFirst(doc.Gifts, nowLocal);
    }

    public async Task<int> FileAsync(uint accountId, IReadOnlyList<AccountGift> gifts, DateTime nowLocal, CancellationToken ct = default)
    {
        if (gifts.Count > 0)
        {
            var update = Builders<AccountGiftDocument>.Update
                .PushEach(d => d.Gifts, gifts)
                .Set(d => d.UpdatedAt, DateTime.UtcNow);
            await _docs.UpdateOneAsync(d => d.Id == accountId, update, new UpdateOptions { IsUpsert = true }, ct);
        }
        return (await GetLiveAsync(accountId, nowLocal, ct)).Count;
    }

    public async Task<bool> TryTakeAsync(uint accountId, string guid, CancellationToken ct = default)
    {
        var result = await _docs.UpdateOneAsync(d => d.Id == accountId,
            Builders<AccountGiftDocument>.Update.PullFilter(d => d.Gifts, g => g.Guid == guid)
                .Set(d => d.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task<AccountAttendance> GetAttendanceAsync(uint accountId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);
        return doc is null
            ? new AccountAttendance(0, 0, 0)
            : new AccountAttendance(doc.AttendedDays, doc.LastAttendedDay, doc.AttendanceStreakBrokenAfter);
    }

    public async Task<bool> TryRecordAttendanceAsync(uint accountId, int lastAttendedDaySeen, int today, int attendedDays,
        int streakBrokenAfter, AccountGift? reward, CancellationToken ct = default)
    {
        var f = Builders<AccountGiftDocument>.Filter;
        var sameDay = lastAttendedDaySeen == 0
            ? f.Or(f.Eq(d => d.LastAttendedDay, 0), f.Exists(d => d.LastAttendedDay, false))
            : f.Eq(d => d.LastAttendedDay, lastAttendedDaySeen);
        var update = Builders<AccountGiftDocument>.Update
            .Set(d => d.AttendedDays, attendedDays)
            .Set(d => d.LastAttendedDay, today)
            .Set(d => d.AttendanceStreakBrokenAfter, streakBrokenAfter)
            .Set(d => d.UpdatedAt, DateTime.UtcNow);
        if (reward is not null)
            update = update.Push(d => d.Gifts, reward);
        try
        {
            // Only a never-attended account may create its document here: one that has attended already has one.
            var result = await _docs.UpdateOneAsync(f.Eq(d => d.Id, accountId) & sameDay, update,
                new UpdateOptions { IsUpsert = lastAttendedDaySeen == 0 }, ct);
            return result.ModifiedCount > 0 || result.UpsertedId is not null;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private static DateTime OldestLiveLocal(DateTime nowLocal) => nowLocal.Date.AddDays(-AccountGiftRules.DaysKept);
}
