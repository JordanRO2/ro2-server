using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PlayTimeRewardRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers read, upsert, unsigned round-trips, UTC kinds and bulk upsert. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresPlayTimeRewardTests
{
    private readonly PostgresFixture _fx;
    public PostgresPlayTimeRewardTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    private static PlayTimeRewardDocument Doc(uint id, uint slot, double seconds, DateTime period, DateTime updated)
        => new() { Id = id, SlotId = slot, AccumulatedSeconds = seconds, PeriodStartUtc = period, UpdatedUtc = updated };

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    [Fact]
    public async Task Get_missing_account_is_null()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        Assert.Null(await repo.GetDocumentAsync(NewId()));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var id = NewId();
        var period = Utc(2026, 10, 18, 5);
        var updated = Utc(2026, 10, 18, 7, 30, 15);
        await repo.SaveDocumentAsync(Doc(id, 4_000_000_001, 1234.5, period, updated));

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(4_000_000_001u, got.SlotId); 
        Assert.Equal(1234.5, got.AccumulatedSeconds);
        Assert.Equal(period, got.PeriodStartUtc);
        Assert.Equal(updated, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.PeriodStartUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
    }

    [Fact]
    public async Task Save_with_large_account_id_round_trips()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var id = 4_294_000_000u + (uint)Random.Shared.Next(0, 900_000);
        await repo.SaveDocumentAsync(Doc(id, 1, 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
    }

    [Fact]
    public async Task Save_overwrites_existing_document_without_duplicating()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveDocumentAsync(Doc(id, 10, 100, Utc(2026, 10, 1), Utc(2026, 10, 1, 1)));
        await repo.SaveDocumentAsync(Doc(id, 20, 250.25, Utc(2026, 10, 2), Utc(2026, 10, 2, 2)));

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(20u, got.SlotId);
        Assert.Equal(250.25, got.AccumulatedSeconds);
        Assert.Equal(Utc(2026, 10, 2), got.PeriodStartUtc);
        Assert.Equal(Utc(2026, 10, 2, 2), got.UpdatedUtc);

        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM account_playtime_reward WHERE account_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)id);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Save_normalizes_local_and_unspecified_kinds_to_utc()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var id = NewId();
        var unspecified = new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Local);
        await repo.SaveDocumentAsync(Doc(id, 1, 1, unspecified, local));

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Utc), got.PeriodStartUtc);
        Assert.Equal(local.ToUniversalTime(), got.UpdatedUtc);
    }

    [Fact]
    public async Task Documents_for_different_accounts_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var a = NewId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        await repo.SaveDocumentAsync(Doc(a, 1, 11, Utc(2026, 10, 1), Utc(2026, 10, 1)));
        await repo.SaveDocumentAsync(Doc(b, 2, 22, Utc(2026, 10, 1), Utc(2026, 10, 1)));
        await repo.SaveDocumentAsync(Doc(a, 3, 33, Utc(2026, 10, 1), Utc(2026, 10, 1)));

        Assert.Equal(3u, (await repo.GetDocumentAsync(a))!.SlotId);
        Assert.Equal(2u, (await repo.GetDocumentAsync(b))!.SlotId);
    }

    [Fact]
    public async Task SaveMany_empty_is_a_noop()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        await repo.SaveManyDocumentsAsync(Array.Empty<PlayTimeRewardDocument>());
    }

    [Fact]
    public async Task SaveMany_inserts_new_and_updates_existing()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var existing = NewId();
        var fresh1 = existing == uint.MaxValue ? existing - 1 : existing + 1;
        var fresh2 = fresh1 == uint.MaxValue ? fresh1 - 2 : fresh1 + 1;
        if (fresh2 == existing) fresh2 = existing - 1;
        await repo.SaveDocumentAsync(Doc(existing, 1, 5, Utc(2026, 10, 1), Utc(2026, 10, 1)));

        await repo.SaveManyDocumentsAsync(new[]
        {
            Doc(existing, 9, 90.5, Utc(2026, 10, 5), Utc(2026, 10, 5, 1)),
            Doc(fresh1, 7, 70, Utc(2026, 10, 5), Utc(2026, 10, 5, 2)),
            Doc(fresh2, 8, 80, Utc(2026, 10, 5), Utc(2026, 10, 5, 3)),
        });

        var e = await repo.GetDocumentAsync(existing);
        Assert.NotNull(e);
        Assert.Equal(9u, e.SlotId);
        Assert.Equal(90.5, e.AccumulatedSeconds);
        Assert.Equal(Utc(2026, 10, 5, 1), e.UpdatedUtc);

        var f1 = await repo.GetDocumentAsync(fresh1);
        Assert.NotNull(f1);
        Assert.Equal(7u, f1.SlotId);
        Assert.Equal(70, f1.AccumulatedSeconds);

        var f2 = await repo.GetDocumentAsync(fresh2);
        Assert.NotNull(f2);
        Assert.Equal(8u, f2.SlotId);
        Assert.Equal(Utc(2026, 10, 5, 3), f2.UpdatedUtc);
    }

    [Fact]
    public async Task SaveMany_duplicate_id_last_document_wins()
    {
        if (!_fx.Available) return;
        var repo = new PlayTimeRewardRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveManyDocumentsAsync(new[]
        {
            Doc(id, 1, 10, Utc(2026, 10, 1), Utc(2026, 10, 1, 1)),
            Doc(id, 2, 20, Utc(2026, 10, 1), Utc(2026, 10, 1, 2)),
        });

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(2u, got.SlotId);
        Assert.Equal(20, got.AccumulatedSeconds);
        Assert.Equal(Utc(2026, 10, 1, 2), got.UpdatedUtc);
    }
}
