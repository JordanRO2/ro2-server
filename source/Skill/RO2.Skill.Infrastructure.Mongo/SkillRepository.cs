using MongoDB.Driver;
using RO2.Skills.Domain;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Skills.Infrastructure.Mongo;

public class SkillRepository : ISkillRepository
{
    /// <summary>Name of the unique (CharacterId, SkillId) index (see <see cref="EnsureIndexesAsync"/>).</summary>
    public const string CharSkillIndexName = "uq_character_skills_char_skill";

    private const int MaxIdAssignAttempts = 5;

    private readonly IMongoCollection<CharacterSkill> _skills;

    // The index is created lazily by the first write of the process (never in the constructor: a synchronous
    // CreateMany there stalls the whole boot when Mongo is down, the MailRepository/AuctionHouse lesson). 1 once
    // the create returned, either way (built, or refused because existing rows collide); a thrown connection error
    // leaves it 0 so the next write tries again.
    private static int s_indexAttempted;

    public SkillRepository(MongoDbContext context)
    {
        _skills = context.GetCollection<CharacterSkill>("character_skills");
    }

    public async Task<IReadOnlyList<CharacterSkill>> GetSkillsAsync(uint characterId)
    {
        return await _skills.Find(s => s.CharacterId == characterId).ToListAsync();
    }

    public async Task LearnSkillAsync(uint characterId, uint skillId, byte level)
    {
        await EnsureIndexOnceAsync();

        // One upsert instead of find-then-insert: an existing row is only ever raised ($max, a lower level never
        // downgrades), a missing one is inserted with the next row id. With the unique (CharacterId, SkillId)
        // index two writers racing for the same skill (login seeding vs a talent spend) cannot leave two rows or
        // throw: the loser's duplicate-key error is retried and then matches the winner's row.
        var filter = Builders<CharacterSkill>.Filter.Eq(s => s.CharacterId, characterId)
            & Builders<CharacterSkill>.Filter.Eq(s => s.SkillId, skillId);
        for (var attempt = 1; ; attempt++)
        {
            var update = Builders<CharacterSkill>.Update
                .Max(s => s.Level, level)
                .SetOnInsert(s => s.Id, await MaxRowIdAsync() + 1);
            try
            {
                await _skills.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
                return;
            }
            catch (MongoWriteException ex) when (attempt < MaxIdAssignAttempts
                                                 && ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
            }
        }
    }

    public async Task UpdateSkillLevelAsync(uint characterId, uint skillId, byte newLevel)
    {
        var update = Builders<CharacterSkill>.Update.Set(s => s.Level, newLevel);
        await _skills.UpdateOneAsync(
            s => s.CharacterId == characterId && s.SkillId == skillId, update);
    }

    public async Task UpsertSkillsAsync(uint characterId, IReadOnlyList<(uint SkillId, byte Level)> skills, CancellationToken ct = default)
    {
        if (skills.Count == 0) return;
        await EnsureIndexOnceAsync();

        var levels = new Dictionary<uint, byte>(skills.Count);
        foreach (var (skillId, level) in skills) levels[skillId] = level;   // a repeated id keeps its last level

        // One ordered bulk write of upserts, each filtered on (CharacterId, SkillId). A duplicate-key error is a
        // row id taken by a concurrent writer between the max read and the write; every op is an idempotent "set
        // this level", so replaying the batch with fresh ids is safe. Not a transaction: a throw part-way can leave
        // the ops before it written, which the caller treats as "unknown" and the next login's read reconciles.
        for (var attempt = 1; ; attempt++)
        {
            var nextId = await MaxRowIdAsync(ct);
            var models = new List<WriteModel<CharacterSkill>>(levels.Count);
            foreach (var (skillId, level) in levels)
            {
                var filter = Builders<CharacterSkill>.Filter.Eq(s => s.CharacterId, characterId)
                    & Builders<CharacterSkill>.Filter.Eq(s => s.SkillId, skillId);
                var update = Builders<CharacterSkill>.Update
                    .Set(s => s.Level, level)
                    .SetOnInsert(s => s.Id, ++nextId);
                models.Add(new UpdateOneModel<CharacterSkill>(filter, update) { IsUpsert = true });
            }

            try
            {
                await _skills.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = true }, ct);
                return;
            }
            catch (MongoBulkWriteException<CharacterSkill> ex) when (attempt < MaxIdAssignAttempts
                && ex.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey))
            {
            }
        }
    }

    public async Task DeleteSkillsAsync(uint characterId, IReadOnlyCollection<uint> skillIds, CancellationToken ct = default)
    {
        var filter = Builders<CharacterSkill>.Filter.Eq(s => s.CharacterId, characterId)
            & Builders<CharacterSkill>.Filter.In(s => s.SkillId, skillIds);
        await _skills.DeleteManyAsync(filter, ct);
    }

    /// <summary>
    /// Creates the unique index on <c>(CharacterId, SkillId)</c>. Idempotent. Returns false (and creates nothing)
    /// when existing rows already hold a duplicate pair, in which case the duplicates must be cleaned up first; the
    /// upserts still work without the index, they just no longer have the backstop. Never throws for that case.
    /// </summary>
    public async Task<bool> EnsureIndexesAsync()
    {
        var keys = Builders<CharacterSkill>.IndexKeys
            .Ascending(s => s.CharacterId).Ascending(s => s.SkillId);
        var model = new CreateIndexModel<CharacterSkill>(keys, new CreateIndexOptions { Name = CharSkillIndexName, Unique = true });
        try
        {
            await _skills.Indexes.CreateOneAsync(model);
            return true;
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            return false;
        }
    }

    private async Task EnsureIndexOnceAsync()
    {
        if (Volatile.Read(ref s_indexAttempted) == 1) return;
        try
        {
            await EnsureIndexesAsync();
            Volatile.Write(ref s_indexAttempted, 1);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // Mongo unreachable or the create was refused for another reason: the write below reports its own
            // failure; try the index again on the next write.
        }
    }

    private async Task<ulong> MaxRowIdAsync(CancellationToken ct = default)
    {
        var maxItem = await _skills.Find(_ => true)
            .SortByDescending(s => s.Id)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        return maxItem?.Id ?? 0;
    }
}
