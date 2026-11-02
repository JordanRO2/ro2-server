using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using RO2.Quests.Domain;
using RO2.Persistence.Mongo.Persistence;
using Testcontainers.MongoDb;
using Xunit;
using MongoQuestRepository = RO2.Quests.Infrastructure.Mongo.QuestRepository;
using PgQuestRepository = RO2.Persistence.Postgres.Repositories.QuestRepository;

namespace RO2.Tests.Integration;

/// <summary>One ephemeral mongo container shared by <see cref="MongoQuestStoredShapeTests"/>; soft-skips without Docker.</summary>
public sealed class MongoQuestFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7").Build();
    public MongoClient? Client { get; private set; }
    public bool Available => Client is not null;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            Client = new MongoClient(ConnectionString);
        }
        catch
        {
            Client = null; // no Docker -> tests soft-skip
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("MongoQuest")]
public sealed class MongoQuestCollection : ICollectionFixture<MongoQuestFixture> { }

/// <summary>
/// Cases both providers must store and return unchanged. They pin the stored shape of <see cref="CharacterQuest"/> /
/// <see cref="QuestStatus"/>, so a pass after the entity moved assemblies proves the move changed no stored byte.
/// </summary>
internal static class QuestShapeCases
{
    // Milliseconds only: Mongo stores BSON datetime at ms; the Postgres test adds a microsecond case of its own.
    public static readonly DateTime Started = new(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);
    public static readonly DateTime Done = new(2026, 3, 5, 6, 7, 8, 987, DateTimeKind.Utc);

    // Verbatim, never re-serialised: odd spacing, newline, unicode and escapes must survive.
    public const string Json = "{ \"kills\" : {\"1001\":3},\n  \"note\":\"café \\\"x\\\" 中\" }";

    public static CharacterQuest Make(ulong id, uint character, uint quest, QuestStatus status,
        string? json, bool traced, DateTime? completed) => new()
        {
            Id = id, CharacterId = character, QuestId = quest, Status = status,
            ProgressJson = json, IsTraced = traced, StartedAt = Started, CompletedAt = completed,
        };

    /// <summary>All four statuses, ids above int.MaxValue, null/non-null/empty progress, both IsTraced values, null/non-null CompletedAt.</summary>
    public static IEnumerable<CharacterQuest> All(uint character, ulong idBase, uint maxQuest = uint.MaxValue)
    {
        yield return Make(idBase + 1, character, maxQuest, QuestStatus.NotStarted, null, false, null);
        yield return Make(idBase + 2, character, 1, QuestStatus.InProgress, Json, true, null);
        yield return Make(idBase + 3, character, 2, QuestStatus.Completed, null, false, Done);
        yield return Make(idBase + 4, character, 3, QuestStatus.Failed, "", true, Done);
    }

    public static void AssertEqual(CharacterQuest e, CharacterQuest a)
    {
        Assert.Equal(e.Id, a.Id);
        Assert.Equal(e.CharacterId, a.CharacterId);
        Assert.Equal(e.QuestId, a.QuestId);
        Assert.Equal(e.Status, a.Status);
        Assert.Equal(e.ProgressJson, a.ProgressJson);
        Assert.Equal(e.IsTraced, a.IsTraced);
        Assert.Equal(e.StartedAt, a.StartedAt);
        Assert.Equal(DateTimeKind.Utc, a.StartedAt.Kind);
        Assert.Equal(e.CompletedAt, a.CompletedAt);
        if (e.CompletedAt is not null) Assert.Equal(DateTimeKind.Utc, a.CompletedAt!.Value.Kind);
    }

    public static uint NewChar() => (uint)Random.Shared.NextInt64(int.MaxValue + 1L, uint.MaxValue);

    // Mongo's UInt32Serializer writes Int32 and throws OverflowException above int.MaxValue (pre-existing provider
    // limit, unchanged by the move), so the Mongo twin keeps every uint id within int.MaxValue.
    public static uint NewMongoChar() => (uint)Random.Shared.NextInt64(1_000_000, int.MaxValue);
    public static ulong NewIdBase() => (ulong)Random.Shared.NextInt64(int.MaxValue + 1L, long.MaxValue / 16) * 8;
}

[Collection("MongoQuest")]
public sealed class MongoQuestStoredShapeTests
{
    private readonly MongoQuestFixture _fx;
    public MongoQuestStoredShapeTests(MongoQuestFixture fx) => _fx = fx;

    private (IMongoCollection<CharacterQuest> typed, IMongoCollection<BsonDocument> raw, MongoDbContext ctx) Open()
    {
        var ctx = new MongoDbContext(_fx.ConnectionString, "ro2_it_quest");
        return (ctx.GetCollection<CharacterQuest>("character_quests"),
            _fx.Client!.GetDatabase("ro2_it_quest").GetCollection<BsonDocument>("character_quests"), ctx);
    }

    [Fact]
    public async Task Every_status_and_field_round_trips_and_the_document_shape_is_pinned()
    {
        if (!_fx.Available) return;
        var (typed, raw, _) = Open();
        var cases = QuestShapeCases.All(QuestShapeCases.NewMongoChar(), QuestShapeCases.NewIdBase(), int.MaxValue).ToList();

        await typed.InsertManyAsync(cases);

        foreach (var e in cases)
        {
            QuestShapeCases.AssertEqual(e, await typed.Find(q => q.Id == e.Id).SingleAsync());

            var doc = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", (long)e.Id)).SingleAsync();
            Assert.Equal(
                new[] { "_id", "CharacterId", "QuestId", "Status", "ProgressJson", "IsTraced", "StartedAt", "CompletedAt" },
                doc.Names.ToArray());
            Assert.Equal(BsonType.Int64, doc["_id"].BsonType);       // ulong id > int.MaxValue -> Int64
            Assert.Equal(BsonType.Int32, doc["CharacterId"].BsonType); // uint -> Int32 (throws above int.MaxValue)
            Assert.Equal(BsonType.Int32, doc["QuestId"].BsonType);
            Assert.Equal(BsonType.Int32, doc["Status"].BsonType);    // the byte enum is stored as its Int32 value
            Assert.Equal((int)e.Status, doc["Status"].AsInt32);
            Assert.Equal(e.ProgressJson is null ? BsonType.Null : BsonType.String, doc["ProgressJson"].BsonType);
            Assert.Equal(e.ProgressJson, doc["ProgressJson"].IsBsonNull ? null : doc["ProgressJson"].AsString);
            Assert.Equal(BsonType.Boolean, doc["IsTraced"].BsonType);
            Assert.Equal(e.IsTraced, doc["IsTraced"].AsBoolean);
            Assert.Equal(BsonType.DateTime, doc["StartedAt"].BsonType);
            Assert.Equal(e.CompletedAt is null ? BsonType.Null : BsonType.DateTime, doc["CompletedAt"].BsonType);
        }
    }

    [Fact]
    public async Task Repository_written_run_reads_back_equal_with_its_stored_shape()
    {
        if (!_fx.Available) return;
        var (typed, raw, ctx) = Open();
        var repo = new MongoQuestRepository(ctx);
        var c = QuestShapeCases.NewMongoChar();

        await repo.AcceptQuestAsync(c, 77);
        await repo.SaveProgressAsync(c, 77, QuestShapeCases.Json);
        await repo.SetQuestsTracedAsync(c, new uint[] { 77 });
        var active = Assert.Single(await repo.GetActiveQuestsAsync(c));
        Assert.Equal(QuestShapeCases.Json, active.ProgressJson);
        Assert.True(active.IsTraced);
        Assert.Equal(DateTimeKind.Utc, active.StartedAt.Kind);

        await repo.CompleteQuestAsync(c, 77);
        var done = await typed.Find(q => q.CharacterId == c).SingleAsync();
        Assert.Equal(QuestStatus.Completed, done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(QuestShapeCases.Json, done.ProgressJson);

        var doc = await raw.Find(Builders<BsonDocument>.Filter.Eq("CharacterId", (int)c)).SingleAsync();
        Assert.Equal(2, doc["Status"].AsInt32);
        Assert.Equal(BsonType.DateTime, doc["CompletedAt"].BsonType);
    }
}

[Collection("Postgres")]
public sealed class PostgresQuestStoredShapeTests
{
    private readonly PostgresFixture _fx;
    public PostgresQuestStoredShapeTests(PostgresFixture fx) => _fx = fx;

    // The id column is GENERATED ALWAYS, so explicit (> int.MaxValue) ids need OVERRIDING SYSTEM VALUE.
    private async Task InsertAsync(CharacterQuest q)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO character_quests (id, character_id, quest_id, status, progress_json, is_traced, started_at, completed_at) " +
            "OVERRIDING SYSTEM VALUE VALUES (@id, @c, @q, @s, @p, @t, @st, @ct)", conn);
        cmd.Parameters.AddWithValue("id", (long)q.Id);
        cmd.Parameters.AddWithValue("c", (long)q.CharacterId);
        cmd.Parameters.AddWithValue("q", (long)q.QuestId);
        cmd.Parameters.AddWithValue("s", (short)q.Status);
        cmd.Parameters.AddWithValue("p", (object?)q.ProgressJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", q.IsTraced);
        cmd.Parameters.AddWithValue("st", q.StartedAt);
        cmd.Parameters.AddWithValue("ct", (object?)q.CompletedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(long Id, long C, long Q, short S, string? P, bool T, DateTime St, DateTime? Ct)> RawAsync(long id)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, character_id, quest_id, status, progress_json, is_traced, started_at, completed_at FROM character_quests WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt16(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.GetBoolean(5), r.GetDateTime(6), r.IsDBNull(7) ? null : r.GetDateTime(7));
    }

    [Fact]
    public async Task Every_status_and_field_is_stored_in_the_same_columns()
    {
        if (!_fx.Available) return;
        var c = QuestShapeCases.NewChar();
        foreach (var e in QuestShapeCases.All(c, QuestShapeCases.NewIdBase()))
        {
            await InsertAsync(e);

            var raw = await RawAsync((long)e.Id);
            Assert.True(raw.Id > int.MaxValue);
            Assert.Equal((long)e.CharacterId, raw.C);
            Assert.Equal((long)e.QuestId, raw.Q);
            Assert.Equal((short)e.Status, raw.S);
            Assert.Equal(e.ProgressJson, raw.P);
            Assert.Equal(e.IsTraced, raw.T);
            Assert.Equal(e.StartedAt, raw.St);
            Assert.Equal(DateTimeKind.Utc, raw.St.Kind);
            Assert.Equal(e.CompletedAt, raw.Ct);

            if (e.Status == QuestStatus.InProgress)
            {
                // Read back through the repository: the whole entity is equal.
                var back = (await new PgQuestRepository(_fx.Db!).GetActiveQuestsAsync(c)).Single(x => x.Id == e.Id);
                QuestShapeCases.AssertEqual(e, back);
            }
        }
    }

    [Fact]
    public async Task Started_at_keeps_microsecond_precision_and_column_types_are_unchanged()
    {
        if (!_fx.Available) return;
        var c = QuestShapeCases.NewChar();
        var id = QuestShapeCases.NewIdBase() + 1;
        var micro = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234560); // .123456
        var e = QuestShapeCases.Make(id, c, 5, QuestStatus.InProgress, null, false, null);
        e.StartedAt = micro;
        await InsertAsync(e);

        var back = Assert.Single(await new PgQuestRepository(_fx.Db!).GetActiveQuestsAsync(c));
        Assert.Equal(micro, back.StartedAt);
        Assert.Equal(id, back.Id);

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'character_quests' ORDER BY ordinal_position", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var cols = new List<string>();
        while (await r.ReadAsync()) cols.Add(r.GetString(0) + ":" + r.GetString(1));
        Assert.Equal(new[]
        {
            "id:bigint", "character_id:bigint", "quest_id:bigint", "status:smallint", "progress_json:text",
            "is_traced:boolean", "started_at:timestamp with time zone", "completed_at:timestamp with time zone",
        }, cols);
    }
}
