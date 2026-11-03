using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using RO2.Domain.Entities;
using RO2.Rankings.Application;
using RO2.Rankings.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Server.Zone.Services;
using Xunit;
using MongoRankingRepository = RO2.Rankings.Infrastructure.Mongo.RankingRepository;
using PgRankingRepository = RO2.Rankings.Infrastructure.Postgres.RankingRepository;

namespace RO2.Tests.Integration;

// B17 S1: the byte-freeze contract for ranking persistence. Whatever is live today is pinned, not "fixed": a later slice that moves the
// serialized types across namespaces must keep every literal below identical.

/// <summary>Raw <c>ranking_scores</c> / <c>ranking_snapshots</c> BSON, the key formats, and the DateTime round-trip on Mongo.</summary>
[Collection("MongoMail")]
public sealed class MongoRankingStoredShapeTests
{
    private const string Db = "ro2_it_ranking_shape";
    private static readonly DateTime Period = new(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc);

    private readonly MongoMailFixture _fx;
    public MongoRankingStoredShapeTests(MongoMailFixture fx) => _fx = fx;

    private MongoRankingRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new MongoRankingRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw(string coll) => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(coll);

    [Fact]
    public void Key_formats_are_pinned_literals()
    {
        var d = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Assert.Equal("3:202603040506:123", RankingKeys.ScoreId(3, d, 123));
        Assert.Equal("3:202603040506", RankingKeys.SnapshotId(3, d));
    }

    [DockerFact]
    public async Task Score_document_has_the_pinned_field_names_bson_types_and_id()
    {
        var repo = Repo();
        var reached = new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc);
        await repo.AddScoreAsync(11, Period, 777_001, 40, reached.AddSeconds(-1));
        await repo.AddScoreAsync(11, Period, 777_001, -15, reached);

        var doc = Raw("ranking_scores").Find(new BsonDocument("_id", "11:202603040506:777001")).Single();
        Assert.Equal(new[] { "_id", "CharId", "PeriodStartUtc", "ReachedUtc", "Score", "Type" }, doc.Names.ToArray());
        Assert.Equal(BsonType.String, doc["_id"].BsonType);
        // CharId is a CLR uint with no serializer: it lands as Int32 (the driver throws above int.MaxValue).
        Assert.Equal(BsonType.Int32, doc["CharId"].BsonType);
        Assert.Equal(777_001, doc["CharId"].AsInt32);
        Assert.Equal(BsonType.Int32, doc["Type"].BsonType);
        Assert.Equal(11, doc["Type"].AsInt32);
        Assert.Equal(BsonType.Int64, doc["Score"].BsonType);
        Assert.Equal(25L, doc["Score"].AsInt64);
        Assert.Equal(BsonType.DateTime, doc["PeriodStartUtc"].BsonType);
        Assert.Equal(BsonType.DateTime, doc["ReachedUtc"].BsonType);
        Assert.Equal(new BsonDateTime(Period), doc["PeriodStartUtc"]);
        Assert.Equal(new BsonDateTime(reached), doc["ReachedUtc"]);
    }

    [DockerFact]
    public async Task Snapshot_document_has_the_pinned_field_names_bson_types_and_id()
    {
        var repo = Repo();
        var rolled = Period.AddSeconds(3);
        await repo.PublishSnapshotAsync(new RankingSnapshotDocument
        {
            Id = RankingKeys.SnapshotId(12, Period), Type = 12, PeriodStartUtc = Period, RolledUtc = rolled,
            Rows = { new RankingSnapshotRow { CharId = 5, Rank = 1, PreviousRank = 2, ClassId = 3, Profession = 7, Score = 9_000_000_000L, Name = "Alpha", Guild = "GuildA" } },
        });

        var doc = Raw("ranking_snapshots").Find(new BsonDocument("_id", "12:202603040506")).Single();
        Assert.Equal(new[] { "_id", "Type", "PeriodStartUtc", "RolledUtc", "Rows" }, doc.Names.ToArray());
        Assert.Equal(BsonType.String, doc["_id"].BsonType);
        Assert.Equal(BsonType.Int32, doc["Type"].BsonType);
        Assert.Equal(BsonType.DateTime, doc["PeriodStartUtc"].BsonType);
        Assert.Equal(BsonType.DateTime, doc["RolledUtc"].BsonType);
        Assert.Equal(BsonType.Array, doc["Rows"].BsonType);
        var row = Assert.Single(doc["Rows"].AsBsonArray).AsBsonDocument;
        Assert.Equal(new[] { "CharId", "Rank", "PreviousRank", "ClassId", "Profession", "Score", "Name", "Guild" }, row.Names.ToArray());
        // CharId / Rank / PreviousRank are CLR uint: Int32 in BSON. ClassId / Profession are byte: Int32. Score is long: Int64.
        Assert.Equal(BsonType.Int32, row["CharId"].BsonType);
        Assert.Equal(BsonType.Int32, row["Rank"].BsonType);
        Assert.Equal(BsonType.Int32, row["PreviousRank"].BsonType);
        Assert.Equal(BsonType.Int32, row["ClassId"].BsonType);
        Assert.Equal(BsonType.Int32, row["Profession"].BsonType);
        Assert.Equal(BsonType.Int64, row["Score"].BsonType);
        Assert.Equal(BsonType.String, row["Name"].BsonType);
        Assert.Equal(BsonType.String, row["Guild"].BsonType);
    }

    [DockerFact]
    public async Task Datetimes_round_trip_as_utc_and_period_equality_holds()
    {
        var repo = Repo();
        var period = new DateTime(2026, 5, 6, 7, 8, 0, DateTimeKind.Utc);
        var rolled = new DateTime(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc).AddTicks(4567);
        var reached = new DateTime(2026, 5, 6, 7, 9, 10, 456, DateTimeKind.Utc);
        await repo.AddScoreAsync(13, period, 1, 1, reached);
        var score = Assert.Single(await repo.GetPeriodScoresAsync(13, period));
        Assert.Equal(reached, score.ReachedUtc);
        Assert.Equal(DateTimeKind.Utc, score.ReachedUtc.Kind);

        await repo.PublishSnapshotAsync(new RankingSnapshotDocument { Id = RankingKeys.SnapshotId(13, period), Type = 13, PeriodStartUtc = period, RolledUtc = rolled });
        var snap = (await repo.GetLatestSnapshotAsync(13))!;
        Assert.Equal(period, snap.PeriodStartUtc);
        Assert.Equal(DateTimeKind.Utc, snap.PeriodStartUtc.Kind);
        // RolledUtc is not re-kinded by the repository; pin what the driver hands back.
        Assert.Equal(rolled.AddTicks(-4567), snap.RolledUtc.ToUniversalTime());
    }
}

/// <summary>Raw Postgres ranking columns and the exact <c>board_rows</c> JSON text, plus the DateTime round-trip.</summary>
[Collection("Postgres")]
public sealed class PostgresRankingStoredShapeTests
{
    private readonly PostgresFixture _fx;
    public PostgresRankingStoredShapeTests(PostgresFixture fx) => _fx = fx;

    private static RankingSnapshotDocument TwoRowBoard(string id, byte type, DateTime period, DateTime rolled) => new()
    {
        Id = id, Type = type, PeriodStartUtc = period, RolledUtc = rolled,
        Rows =
        {
            new RankingSnapshotRow { CharId = 4_000_000_000u, Rank = 1, PreviousRank = 2, ClassId = 3, Profession = 7, Score = 9_000_000_000L, Name = "Alpha", Guild = "GuildA" },
            new RankingSnapshotRow { CharId = 2, Rank = 2, PreviousRank = 0, ClassId = 4, Profession = 8, Score = 50, Name = "Beta", Guild = "" },
        },
    };

    private const string CompactJson =
        "[{\"CharId\":4000000000,\"Rank\":1,\"PreviousRank\":2,\"ClassId\":3,\"Profession\":7,\"Score\":9000000000,\"Name\":\"Alpha\",\"Guild\":\"GuildA\"}," +
        "{\"CharId\":2,\"Rank\":2,\"PreviousRank\":0,\"ClassId\":4,\"Profession\":8,\"Score\":50,\"Name\":\"Beta\",\"Guild\":\"\"}]";

    private const string JsonbExpected =
        "[{\"Name\": \"Alpha\", \"Rank\": 1, \"Guild\": \"GuildA\", \"Score\": 9000000000, \"CharId\": 4000000000, \"ClassId\": 3, \"Profession\": 7, \"PreviousRank\": 2}, {\"Name\": \"Beta\", \"Rank\": 2, \"Guild\": \"\", \"Score\": 50, \"CharId\": 2, \"ClassId\": 4, \"Profession\": 8, \"PreviousRank\": 0}]";

    [Fact]
    public void Serialized_board_rows_text_is_a_pinned_literal()
    {
        var board = TwoRowBoard("x", 1, DateTime.UtcNow, DateTime.UtcNow);
        Assert.Equal(CompactJson, JsonSerializer.Serialize(board.Rows));
    }

    [Fact]
    public void Snapshot_id_key_format_is_invariant()
    {
        Assert.Equal("5:202611300000", RankingKeys.SnapshotId(5, new DateTime(2026, 11, 30, 0, 0, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Board_rows_jsonb_text_and_columns_are_pinned()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new PgRankingRepository(_fx.Db!);
        var period = new DateTime(2040, 1, 2, 3, 4, 0, DateTimeKind.Utc);
        var rolled = new DateTime(2040, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(1234);
        await repo.PublishSnapshotAsync(TwoRowBoard("90:204001020304", 90, period, rolled));

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, ranking_type, board_rows::text FROM ranking_snapshots WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", "90:204001020304");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("90:204001020304", r.GetString(0));
        Assert.Equal((short)90, r.GetInt16(1));
        // jsonb re-serializes: array order is kept, object keys are re-ordered and spaced by Postgres.
        Assert.Equal(JsonbExpected, r.GetString(2));
        await r.CloseAsync();

        var back = (await repo.GetLatestSnapshotAsync(90))!;
        Assert.Equal(CompactJson, JsonSerializer.Serialize(back.Rows));
    }

    [Fact]
    public async Task Datetimes_round_trip_utc_truncated_to_milliseconds_and_period_equality_holds()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new PgRankingRepository(_fx.Db!);
        var period = new DateTime(2041, 5, 6, 7, 8, 0, DateTimeKind.Utc);
        var rolled = new DateTime(2041, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc).AddTicks(4567);
        var reached = new DateTime(2041, 5, 6, 7, 9, 10, 456, DateTimeKind.Utc).AddTicks(999);
        await repo.AddScoreAsync(91, period, 1, 1, reached);
        var score = Assert.Single(await repo.GetPeriodScoresAsync(91, period));
        Assert.Equal(reached.AddTicks(-999), score.ReachedUtc);
        Assert.Equal(DateTimeKind.Utc, score.ReachedUtc.Kind);

        await repo.PublishSnapshotAsync(new RankingSnapshotDocument { Id = RankingKeys.SnapshotId(91, period), Type = 91, PeriodStartUtc = period, RolledUtc = rolled });
        var snap = (await repo.GetLatestSnapshotAsync(91))!;
        Assert.Equal(period, snap.PeriodStartUtc);
        Assert.Equal(rolled.AddTicks(-4567), snap.RolledUtc);
        Assert.Equal(DateTimeKind.Utc, snap.RolledUtc.Kind);
    }
}
