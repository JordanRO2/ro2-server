using RO2.Dungeons.Application;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using RO2.Persistence.Mongo.Persistence;
using RO2.Dungeons.Infrastructure.Mongo;
using Xunit;
using PgRepo = RO2.Dungeons.Infrastructure.Postgres.DungeonEntryCountRepository;

namespace RO2.Tests.Integration;

// B18 A-S1: the byte-freeze contract for dungeon entry-count persistence. Whatever is live today is pinned, not "fixed": a later slice
// that moves the serialized type / repos must keep every literal below identical.

/// <summary>Raw <c>character_dungeon_entries</c> BSON: field names, BSON types, the uint <c>_id</c>, the <c>Counts</c> sub-document.</summary>
[Collection("MongoMail")]
public sealed class MongoDungeonEntryCountStoredShapeTests
{
    private const string Db = "ro2_it_dungeon_entries_shape";
    private static readonly DateTime Day1 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly MongoMailFixture _fx;
    public MongoDungeonEntryCountStoredShapeTests(MongoMailFixture fx) => _fx = fx;

    private DungeonEntryCountRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new DungeonEntryCountRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw() =>
        _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>("character_dungeon_entries");

    [DockerFact]
    public async Task Document_has_the_pinned_field_names_bson_types_and_id()
    {
        var repo = Repo();
        var before = DateTime.UtcNow.AddSeconds(-1);
        await repo.RecordEntryAsync(5001, 11001, Day1);
        await repo.RecordEntryAsync(5001, 11001, Day1);
        await repo.RecordEntryAsync(5001, 4_000_001, Day1);

        var doc = Raw().Find(new BsonDocument("_id", 5001)).Single();
        Assert.Equal(new[] { "_id", "WindowStartUtc", "Counts", "UpdatedUtc" }, doc.Names.ToArray());
        // _id is the CLR uint characterId with no serializer: it lands as Int32 (Int64 above int.MaxValue).
        Assert.Equal(BsonType.Int32, doc["_id"].BsonType);
        Assert.Equal(5001, doc["_id"].AsInt32);

        Assert.Equal(BsonType.DateTime, doc["WindowStartUtc"].BsonType);
        Assert.Equal(new BsonDateTime(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc)), doc["WindowStartUtc"]);

        // Counts is a sub-document keyed by the decimal DungeonID string, int32 values.
        Assert.Equal(BsonType.Document, doc["Counts"].BsonType);
        var counts = doc["Counts"].AsBsonDocument;
        Assert.Equal(new[] { "11001", "4000001" }, counts.Names.ToArray());
        Assert.Equal(BsonType.Int32, counts["11001"].BsonType);
        Assert.Equal(2, counts["11001"].AsInt32);
        Assert.Equal(BsonType.Int32, counts["4000001"].BsonType);
        Assert.Equal(1, counts["4000001"].AsInt32);

        // UpdatedUtc is DateTime.UtcNow at write time, NOT the utcNow parameter (Day1, in 2026-07).
        Assert.Equal(BsonType.DateTime, doc["UpdatedUtc"].BsonType);
        var updated = doc["UpdatedUtc"].ToUniversalTime();
        Assert.NotEqual(doc["WindowStartUtc"].ToUniversalTime(), updated);
        Assert.NotEqual(Day1, updated);
        Assert.InRange(updated, before, DateTime.UtcNow.AddSeconds(1));
    }

    [DockerFact]
    public async Task A_new_window_replaces_the_whole_counts_document()
    {
        var repo = Repo();
        await repo.RecordEntryAsync(5002, 11001, Day1);
        await repo.RecordEntryAsync(5002, 11002, Day1);
        await repo.RecordEntryAsync(5002, 11003, Day1.AddDays(1));

        var doc = Assert.Single(Raw().Find(new BsonDocument("_id", 5002)).ToList());
        Assert.Equal(new[] { "11003" }, doc["Counts"].AsBsonDocument.Names.ToArray());
        Assert.Equal(new BsonDateTime(new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc)), doc["WindowStartUtc"]);
    }
}

/// <summary>Raw Postgres <c>character_dungeon_entries</c> columns and the <c>counts</c> jsonb map.</summary>
[Collection("Postgres")]
public sealed class PostgresDungeonEntryCountStoredShapeTests
{
    private static readonly DateTime Day1 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fx;
    public PostgresDungeonEntryCountStoredShapeTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Columns_types_and_primary_key_are_pinned()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cols = new NpgsqlCommand(
            "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = 'character_dungeon_entries' ORDER BY ordinal_position", conn);
        var got = new List<string>();
        await using (var r = await cols.ExecuteReaderAsync())
            while (await r.ReadAsync()) got.Add($"{r.GetString(0)}:{r.GetString(1)}:{r.GetString(2)}");
        Assert.Equal(new[]
        {
            "character_id:bigint:NO", "window_start_utc:timestamp with time zone:NO",
            "counts:jsonb:NO", "updated_utc:timestamp with time zone:NO",
        }, got);

        await using var pk = new NpgsqlCommand(
            "SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) " +
            "WHERE i.indrelid = 'character_dungeon_entries'::regclass AND i.indisprimary", conn);
        Assert.Equal("character_id", (string?)await pk.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Row_holds_the_window_the_counts_map_and_a_fresh_updated_utc()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new PgRepo(_fx.Db!);
        uint id = 4_000_000_000u + (uint)Random.Shared.Next(0, 100_000_000); // above int.MaxValue
        var before = DateTime.UtcNow.AddSeconds(-1);
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 4_000_001, Day1);

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT character_id, window_start_utc, counts::text, updated_utc FROM character_dungeon_entries WHERE character_id = @c", conn);
        cmd.Parameters.AddWithValue("c", (long)id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal((long)id, r.GetInt64(0));
        Assert.True(r.GetInt64(0) > int.MaxValue);
        Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc), r.GetDateTime(1));

        // jsonb re-serializes: keys are spaced by Postgres, shorter keys first.
        Assert.Equal("{\"11001\": 2, \"4000001\": 1}", r.GetString(2));
        var updated = r.GetDateTime(3);
        Assert.NotEqual(r.GetDateTime(1), updated);
        Assert.InRange(updated, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task A_new_window_replaces_the_whole_counts_object()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new PgRepo(_fx.Db!);
        uint id = 3_000_000_000u + (uint)Random.Shared.Next(0, 100_000_000);
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11002, Day1);
        await repo.RecordEntryAsync(id, 11003, Day1.AddDays(1));

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT counts::text FROM character_dungeon_entries WHERE character_id = @c", conn);
        cmd.Parameters.AddWithValue("c", (long)id);
        Assert.Equal("{\"11003\": 1}", (string?)await cmd.ExecuteScalarAsync());
    }
}
