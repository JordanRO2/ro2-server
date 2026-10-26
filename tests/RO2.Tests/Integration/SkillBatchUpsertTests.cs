using MongoDB.Driver;
using RO2.Skills.Domain;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using Xunit;
using MongoSkillRepository = RO2.Skills.Infrastructure.Mongo.SkillRepository;
using PgSkillRepository = RO2.Skills.Infrastructure.Postgres.SkillRepository;

namespace RO2.Tests.Integration;

/// <summary>
/// CP-G6/G7 persistence: <c>ISkillRepository.UpsertSkillsAsync</c> (one batch of exact-level upserts), the idempotent
/// <c>LearnSkillAsync</c> and the unique (character, skill) key, against a live PostgreSQL (the shared
/// <see cref="PostgresFixture"/>) and a live Mongo at localhost:27017. Both soft-skip when their server is not there.
/// </summary>
[Collection("Postgres")]
public sealed class SkillBatchUpsertTests
{
    private readonly PostgresFixture _fx;
    public SkillBatchUpsertTests(PostgresFixture fx) => _fx = fx;

    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(3_000_000_000L, 4_200_000_000L);

    // ---- Postgres -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Postgres_Upsert_InsertsNewRows_SetsExactLevels_AndLowersOnPurpose()
    {
        if (!_fx.Available) return;
        var repo = new PgSkillRepository(_fx.Db!);
        var ch = NewCharacterId();
        await repo.LearnSkillAsync(ch, 1110, 3);

        await repo.UpsertSkillsAsync(ch, [(1110u, (byte)2), (1102u, (byte)5), (1117u, (byte)1)]);

        var rows = (await repo.GetSkillsAsync(ch)).ToDictionary(r => r.SkillId, r => r.Level);
        Assert.Equal(new Dictionary<uint, byte> { [1110] = 2, [1102] = 5, [1117] = 1 }, rows);
    }

    [Fact]
    public async Task Postgres_Upsert_WithARepeatedId_KeepsTheLastLevel_AndOneRow()
    {
        if (!_fx.Available) return;
        var repo = new PgSkillRepository(_fx.Db!);
        var ch = NewCharacterId();

        await repo.UpsertSkillsAsync(ch, [(1102u, (byte)2), (1102u, (byte)4)]);

        var row = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal((byte)4, row.Level);
    }

    [Fact]
    public async Task Postgres_Upsert_Empty_WritesNothing()
    {
        if (!_fx.Available) return;
        var repo = new PgSkillRepository(_fx.Db!);
        var ch = NewCharacterId();

        await repo.UpsertSkillsAsync(ch, []);

        Assert.Empty(await repo.GetSkillsAsync(ch));
    }

    [Fact]
    public async Task Postgres_ConcurrentLearnOfTheSameSkill_LeavesOneRow()
    {
        if (!_fx.Available) return;
        var repo = new PgSkillRepository(_fx.Db!);
        var ch = NewCharacterId();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => repo.LearnSkillAsync(ch, 1102, (byte)(1 + i % 3))));

        var row = Assert.Single(await repo.GetSkillsAsync(ch));
        Assert.Equal((byte)3, row.Level);
    }

    // ---- Mongo ----------------------------------------------------------------------------------------------

    private const string MongoConnection = "mongodb://localhost:27017";

    private static bool MongoUp()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(MongoConnection);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            _ = new MongoClient(settings).ListDatabaseNames(new ListDatabaseNamesOptions());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            return false;
        }
    }

    private static async Task WithMongoAsync(Func<MongoSkillRepository, IMongoCollection<CharacterSkill>, Task> body)
    {
        if (!MongoUp()) return;
        var dbName = "ro2_test_skills_" + Guid.NewGuid().ToString("N");
        try
        {
            var context = new MongoDbContext(MongoConnection, dbName);
            await body(new MongoSkillRepository(context), context.GetCollection<CharacterSkill>("character_skills"));
        }
        finally
        {
            await new MongoClient(MongoConnection).DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public Task Mongo_Upsert_WritesTheBatchWithDistinctRowIds_AndSetsExactLevels() => WithMongoAsync(async (repo, skills) =>
    {
        await repo.LearnSkillAsync(55, 1110, 3);

        await repo.UpsertSkillsAsync(55, [(1110u, (byte)2), (1102u, (byte)5), (1117u, (byte)1), (1102u, (byte)4)]);

        var rows = await repo.GetSkillsAsync(55);
        Assert.Equal(new Dictionary<uint, byte> { [1110] = 2, [1102] = 4, [1117] = 1 }, rows.ToDictionary(r => r.SkillId, r => r.Level));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());   // every row has its own _id
        Assert.Equal(3, await skills.CountDocumentsAsync(Builders<CharacterSkill>.Filter.Empty));
    });

    [Fact]
    public Task Mongo_TheUniqueIndexIsBuilt_AndRejectsASecondRowOfOnePair() => WithMongoAsync(async (repo, skills) =>
    {
        Assert.True(await repo.EnsureIndexesAsync());
        await skills.InsertOneAsync(new CharacterSkill { Id = 1, CharacterId = 9, SkillId = 1102, Level = 1 });

        await Assert.ThrowsAsync<MongoWriteException>(() =>
            skills.InsertOneAsync(new CharacterSkill { Id = 2, CharacterId = 9, SkillId = 1102, Level = 2 }));
    });

    [Fact]
    public Task Mongo_PreExistingDuplicates_MakeEnsureIndexesReturnFalse_NotThrow() => WithMongoAsync(async (repo, skills) =>
    {
        await skills.InsertManyAsync([
            new CharacterSkill { Id = 1, CharacterId = 9, SkillId = 1102, Level = 1 },
            new CharacterSkill { Id = 2, CharacterId = 9, SkillId = 1102, Level = 2 },
        ]);

        Assert.False(await repo.EnsureIndexesAsync());
    });

    [Fact]
    public Task Mongo_ConcurrentLearnOfOneSkill_LeavesExactlyOneRow_AtTheHighestLevel() => WithMongoAsync(async (repo, skills) =>
    {
        Assert.True(await repo.EnsureIndexesAsync());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => repo.LearnSkillAsync(77, 1102, (byte)(1 + i % 3))));

        var row = Assert.Single(await repo.GetSkillsAsync(77));
        Assert.Equal((byte)3, row.Level);
    });

    [Fact]
    public Task Mongo_LearnNeverDowngrades() => WithMongoAsync(async (repo, skills) =>
    {
        await repo.LearnSkillAsync(78, 1102, 4);
        await repo.LearnSkillAsync(78, 1102, 2);

        Assert.Equal((byte)4, Assert.Single(await repo.GetSkillsAsync(78)).Level);
    });
}
