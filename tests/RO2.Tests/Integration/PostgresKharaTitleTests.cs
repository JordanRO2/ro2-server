using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="KharaTitleRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the missing-document read, whole-document upsert and the play-clock update. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresKharaTitleTests
{
    private readonly PostgresFixture _fx;
    public PostgresKharaTitleTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_200_000_000L);

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    [Fact]
    public async Task Get_unknown_character_is_null()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        var doc = new KharaTitleDocument
        {
            Id = id,
            KharaA = 4_000_000_001u, 
            KharaB = 77,
            ATimerStartUtc = Utc(2026, 3, 4, 5, 6, 7),
            LastSuccessUtc = Utc(2026, 3, 4, 6, 7, 8),
            PlayClockUtc = Utc(2026, 3, 4, 7, 8, 9),
            UpdatedUtc = Utc(2026, 3, 4, 8, 9, 10),
        };
        await repo.SaveAsync(doc);

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(4_000_000_001u, got.KharaA);
        Assert.Equal(77u, got.KharaB);
        Assert.Equal(doc.ATimerStartUtc, got.ATimerStartUtc);
        Assert.Equal(doc.LastSuccessUtc, got.LastSuccessUtc);
        Assert.Equal(doc.PlayClockUtc, got.PlayClockUtc);
        Assert.Equal(doc.UpdatedUtc, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, got.PlayClockUtc!.Value.Kind);
    }

    [Fact]
    public async Task Save_with_null_instants_reads_back_null()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaTitleDocument { Id = id, KharaA = 0, KharaB = 0, UpdatedUtc = Utc(2026, 1, 2, 3, 4, 5) });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(0u, got.KharaA);
        Assert.Equal(0u, got.KharaB);
        Assert.Null(got.ATimerStartUtc);
        Assert.Null(got.LastSuccessUtc);
        Assert.Null(got.PlayClockUtc);
        Assert.Equal(Utc(2026, 1, 2, 3, 4, 5), got.UpdatedUtc);
    }

    [Fact]
    public async Task Save_twice_replaces_whole_document_including_back_to_null()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id,
            KharaA = 10,
            KharaB = 20,
            ATimerStartUtc = Utc(2026, 5, 1, 0, 0, 0),
            LastSuccessUtc = Utc(2026, 5, 1, 0, 0, 1),
            PlayClockUtc = Utc(2026, 5, 1, 0, 0, 2),
            UpdatedUtc = Utc(2026, 5, 1, 0, 0, 3),
        });
        
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id,
            KharaA = 11,
            KharaB = 0,
            ATimerStartUtc = null,
            LastSuccessUtc = Utc(2026, 6, 1, 0, 0, 1),
            PlayClockUtc = null,
            UpdatedUtc = Utc(2026, 6, 1, 0, 0, 3),
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(11u, got.KharaA);
        Assert.Equal(0u, got.KharaB);
        Assert.Null(got.ATimerStartUtc);
        Assert.Equal(Utc(2026, 6, 1, 0, 0, 1), got.LastSuccessUtc);
        Assert.Null(got.PlayClockUtc);
        Assert.Equal(Utc(2026, 6, 1, 0, 0, 3), got.UpdatedUtc);
    }

    [Fact]
    public async Task Save_truncates_to_milliseconds_and_normalises_local_to_utc()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        var withTicks = Utc(2026, 7, 8, 9, 10, 11).AddTicks(1_234_567); 
        var local = Utc(2026, 7, 8, 12, 0, 0).ToLocalTime();
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id,
            UpdatedUtc = withTicks,
            PlayClockUtc = local,
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(Utc(2026, 7, 8, 9, 10, 11).AddMilliseconds(123), got.UpdatedUtc);
        Assert.Equal(Utc(2026, 7, 8, 12, 0, 0), got.PlayClockUtc);
        Assert.Equal(DateTimeKind.Utc, got.PlayClockUtc!.Value.Kind);
    }

    [Fact]
    public async Task StorePlayClock_updates_only_clock_and_updated_stamp()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id,
            KharaA = 5,
            KharaB = 6,
            ATimerStartUtc = Utc(2026, 4, 1, 1, 1, 1),
            LastSuccessUtc = Utc(2026, 4, 1, 2, 2, 2),
            PlayClockUtc = Utc(2026, 4, 1, 3, 3, 3),
            UpdatedUtc = Utc(2026, 4, 1, 4, 4, 4),
        });

        var newClock = Utc(2026, 4, 2, 10, 0, 0);
        var newUpdated = Utc(2026, 4, 2, 11, 0, 0);
        await repo.StorePlayClockAsync(id, newClock, newUpdated);

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(5u, got.KharaA);
        Assert.Equal(6u, got.KharaB);
        Assert.Equal(Utc(2026, 4, 1, 1, 1, 1), got.ATimerStartUtc);
        Assert.Equal(Utc(2026, 4, 1, 2, 2, 2), got.LastSuccessUtc);
        Assert.Equal(newClock, got.PlayClockUtc);
        Assert.Equal(newUpdated, got.UpdatedUtc);
    }

    [Fact]
    public async Task StorePlayClock_sets_clock_on_document_that_had_none()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaTitleDocument { Id = id, KharaA = 9, UpdatedUtc = Utc(2026, 2, 2, 2, 2, 2) });

        await repo.StorePlayClockAsync(id, Utc(2026, 2, 3, 0, 0, 0), Utc(2026, 2, 3, 0, 0, 1));

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(9u, got.KharaA);
        Assert.Equal(Utc(2026, 2, 3, 0, 0, 0), got.PlayClockUtc);
        Assert.Equal(Utc(2026, 2, 3, 0, 0, 1), got.UpdatedUtc);
    }

    [Fact]
    public async Task StorePlayClock_for_missing_character_creates_no_document()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var id = NewCharacterId();

        await repo.StorePlayClockAsync(id, Utc(2026, 8, 1, 0, 0, 0), Utc(2026, 8, 1, 0, 0, 1));

        Assert.Null(await repo.GetAsync(id)); 
    }

    [Fact]
    public async Task Documents_of_different_characters_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new KharaTitleRepository(_fx.Db!);
        var idA = NewCharacterId();
        var idB = idA + 1;
        await repo.SaveAsync(new KharaTitleDocument { Id = idA, KharaA = 1, UpdatedUtc = Utc(2026, 9, 1, 0, 0, 0) });
        await repo.SaveAsync(new KharaTitleDocument { Id = idB, KharaA = 2, UpdatedUtc = Utc(2026, 9, 1, 0, 0, 0) });

        await repo.StorePlayClockAsync(idA, Utc(2026, 9, 2, 0, 0, 0), Utc(2026, 9, 2, 0, 0, 1));

        var a = await repo.GetAsync(idA);
        var b = await repo.GetAsync(idB);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(1u, a.KharaA);
        Assert.Equal(Utc(2026, 9, 2, 0, 0, 0), a.PlayClockUtc);
        Assert.Equal(2u, b.KharaA);
        Assert.Null(b.PlayClockUtc);
    }
}
