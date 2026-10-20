using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CardBookRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the default for a book not yet stored, whole-document upsert and
/// replace, entry order and field fidelity (uint and ulong ranges, millisecond UTC timestamps), the empty book,
/// and per-character isolation.
/// </summary>
/// <remarks>Each test uses a fresh random character id above <see cref="int.MaxValue"/> to exercise the
/// uint-to-bigint mapping.</remarks>
[Collection("Postgres")]
public sealed class PostgresCardBookTests
{
    private readonly PostgresFixture _fx;
    public PostgresCardBookTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)(3_000_000_000L + Random.Shared.NextInt64(0, 1_000_000_000));

    private static DateTime Ms(int offsetSeconds = 0)
    {
        var now = DateTime.UtcNow.AddSeconds(offsetSeconds);
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private async Task<long> RowCountAsync(uint id)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM card_books WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Get_unknown_character_returns_fresh_default_and_does_not_store_it()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var id = NewId();

        var doc = await repo.GetOrCreateAsync(id);

        Assert.Equal(id, doc.Id);
        Assert.Empty(doc.Cards);
        Assert.Equal(0, await RowCountAsync(id));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field_in_order()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var id = NewId();
        var t1 = Ms(-30);
        var t2 = Ms(-10);
        var doc = new CardBookDocument
        {
            Id = id,
            Cards =
            {
                new CardBookEntry { CardId = 22310001, Grade = 3, EnforceGrade = 4, Kind = 1, OriginItemUid = ulong.MaxValue, RegisteredAt = t1 },
                new CardBookEntry { CardId = 4_000_000_001, Grade = 5, EnforceGrade = 0, Kind = 0, OriginItemUid = 42, RegisteredAt = t2 },
                new CardBookEntry { CardId = 22310001, Grade = 3, EnforceGrade = 4, Kind = 0, OriginItemUid = 7, RegisteredAt = t2 },
            },
        };

        await repo.SaveAsync(doc);
        var loaded = await repo.GetOrCreateAsync(id);

        Assert.Equal(id, loaded.Id);
        Assert.Equal(3, loaded.Cards.Count);

        Assert.Equal(22310001u, loaded.Cards[0].CardId);
        Assert.Equal((byte)3, loaded.Cards[0].Grade);
        Assert.Equal((byte)4, loaded.Cards[0].EnforceGrade);
        Assert.Equal((byte)1, loaded.Cards[0].Kind);
        Assert.Equal(ulong.MaxValue, loaded.Cards[0].OriginItemUid);
        Assert.Equal(t1, loaded.Cards[0].RegisteredAt);
        Assert.Equal(DateTimeKind.Utc, loaded.Cards[0].RegisteredAt.Kind);

        Assert.Equal(4_000_000_001u, loaded.Cards[1].CardId);
        Assert.Equal((byte)5, loaded.Cards[1].Grade);
        Assert.Equal((byte)0, loaded.Cards[1].EnforceGrade);
        Assert.Equal((byte)0, loaded.Cards[1].Kind);
        Assert.Equal(42ul, loaded.Cards[1].OriginItemUid);
        Assert.Equal(t2, loaded.Cards[1].RegisteredAt);

        
        Assert.Equal(22310001u, loaded.Cards[2].CardId);
        Assert.Equal((byte)0, loaded.Cards[2].Kind);
        Assert.Equal(7ul, loaded.Cards[2].OriginItemUid);
    }

    [Fact]
    public async Task Save_truncates_timestamp_to_utc_milliseconds()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var id = NewId();
        var local = new DateTime(2026, 7, 1, 12, 30, 45, 123, DateTimeKind.Utc).AddTicks(4567).ToLocalTime();
        var doc = new CardBookDocument { Id = id };
        doc.Cards.Add(new CardBookEntry { CardId = 1, Grade = 2, RegisteredAt = local });

        await repo.SaveAsync(doc);
        var loaded = await repo.GetOrCreateAsync(id);

        var got = Assert.Single(loaded.Cards).RegisteredAt;
        Assert.Equal(DateTimeKind.Utc, got.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 12, 30, 45, 123, DateTimeKind.Utc), got);
    }

    [Fact]
    public async Task Save_replaces_the_whole_document_and_keeps_one_row()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var id = NewId();
        var first = new CardBookDocument { Id = id };
        first.Cards.Add(new CardBookEntry { CardId = 100, Grade = 2, OriginItemUid = 1, RegisteredAt = Ms() });
        first.Cards.Add(new CardBookEntry { CardId = 200, Grade = 3, OriginItemUid = 2, RegisteredAt = Ms() });
        await repo.SaveAsync(first);

        
        var second = new CardBookDocument { Id = id };
        second.Cards.Add(new CardBookEntry { CardId = 200, Grade = 3, OriginItemUid = 2, RegisteredAt = Ms() });
        second.Cards.Add(new CardBookEntry { CardId = 300, Grade = 4, EnforceGrade = 2, OriginItemUid = 3, RegisteredAt = Ms() });
        await repo.SaveAsync(second);

        var loaded = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 200, 300 }, loaded.Cards.Select(c => c.CardId).ToArray());
        Assert.Equal((byte)2, loaded.Cards[1].EnforceGrade);
        Assert.Equal(1, await RowCountAsync(id));
    }

    [Fact]
    public async Task Saving_an_empty_book_is_stored_and_loads_empty()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var id = NewId();
        var doc = new CardBookDocument { Id = id };
        doc.Cards.Add(new CardBookEntry { CardId = 5, Grade = 2, RegisteredAt = Ms() });
        await repo.SaveAsync(doc);

        
        await repo.SaveAsync(new CardBookDocument { Id = id });

        var loaded = await repo.GetOrCreateAsync(id);
        Assert.Equal(id, loaded.Id);
        Assert.Empty(loaded.Cards);
        Assert.Equal(1, await RowCountAsync(id));
    }

    [Fact]
    public async Task Characters_have_independent_books()
    {
        if (!_fx.Available) return;
        var repo = new CardBookRepository(_fx.Db!);
        var a = NewId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        var docA = new CardBookDocument { Id = a };
        docA.Cards.Add(new CardBookEntry { CardId = 11, Grade = 2, RegisteredAt = Ms() });
        var docB = new CardBookDocument { Id = b };
        docB.Cards.Add(new CardBookEntry { CardId = 22, Grade = 3, RegisteredAt = Ms() });
        docB.Cards.Add(new CardBookEntry { CardId = 33, Grade = 4, RegisteredAt = Ms() });

        await repo.SaveAsync(docA);
        await repo.SaveAsync(docB);

        Assert.Equal(new uint[] { 11 }, (await repo.GetOrCreateAsync(a)).Cards.Select(c => c.CardId).ToArray());
        Assert.Equal(new uint[] { 22, 33 }, (await repo.GetOrCreateAsync(b)).Cards.Select(c => c.CardId).ToArray());
    }
}
