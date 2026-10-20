using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="KharaStateRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the point balance, accepted list, completed ids, whole-document upsert, per-character isolation and UTC timestamps. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresKharaStateTests
{
    private readonly PostgresFixture _fx;
    public PostgresKharaStateTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_200_000_000L);

    [Fact]
    public async Task Get_unknown_character_is_null()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        var updated = new DateTime(2026, 10, 18, 12, 30, 45, 123, DateTimeKind.Utc);
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Points = 4_000_000_000u,
            Active =
            {
                new KharaActiveDocument { KharaId = 7, State = 2, Counts = { 0, 5, 4_000_000_001u } },
                new KharaActiveDocument { KharaId = 4_100_000_000u, State = 3, Counts = { 9 } },
            },
            Completed = { 1, 3, 4_200_000_000u },
            UpdatedUtc = updated,
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);
        Assert.Equal(4_000_000_000u, got.Points);
        Assert.Equal(2, got.Active.Count);
        Assert.Equal(7u, got.Active[0].KharaId);
        Assert.Equal(2u, got.Active[0].State);
        Assert.Equal(new uint[] { 0, 5, 4_000_000_001u }, got.Active[0].Counts);
        Assert.Equal(4_100_000_000u, got.Active[1].KharaId);
        Assert.Equal(3u, got.Active[1].State);
        Assert.Equal(new uint[] { 9 }, got.Active[1].Counts);
        Assert.Equal(new uint[] { 1, 3, 4_200_000_000u }, got.Completed);
        Assert.Equal(updated, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
    }

    [Fact]
    public async Task Save_preserves_list_order_and_helpers_work_on_the_loaded_document()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Active =
            {
                new KharaActiveDocument { KharaId = 10, Counts = { 3, 2, 1 } },
                new KharaActiveDocument { KharaId = 20 },
                new KharaActiveDocument { KharaId = 30, Counts = { 1 } },
            },
            Completed = { 2, 4, 6, 8 },
            UpdatedUtc = DateTime.UtcNow,
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(new uint[] { 10, 20, 30 }, got!.Active.Select(a => a.KharaId).ToArray());
        Assert.Equal(new uint[] { 3, 2, 1 }, got.FindActive(10)!.Counts);
        Assert.Empty(got.FindActive(20)!.Counts);
        Assert.Equal(KharaActiveDocument.StateInProgress, got.FindActive(20)!.State);
        Assert.Null(got.FindActive(99));
        Assert.True(got.IsCompleted(6));
        Assert.False(got.IsCompleted(5));
    }

    [Fact]
    public async Task Save_empty_state_round_trips_to_empty_lists()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaStateDocument { Id = id, UpdatedUtc = DateTime.UtcNow });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(0u, got!.Points);
        Assert.Empty(got.Active);
        Assert.Empty(got.Completed);
    }

    [Fact]
    public async Task Save_twice_replaces_the_whole_document_without_merging()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Points = 100,
            Active = { new KharaActiveDocument { KharaId = 1, Counts = { 1, 2 } }, new KharaActiveDocument { KharaId = 2 } },
            Completed = { 5, 6 },
            UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var second = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Points = 7,
            Active = { new KharaActiveDocument { KharaId = 9, State = 3, Counts = { 4 } } },
            Completed = { 1 },
            UpdatedUtc = second,
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(7u, got!.Points);
        var only = Assert.Single(got.Active);
        Assert.Equal(9u, only.KharaId);
        Assert.Equal(3u, only.State);
        Assert.Equal(new uint[] { 4 }, only.Counts);
        Assert.Equal(new uint[] { 1 }, got.Completed);
        Assert.Equal(second, got.UpdatedUtc);
    }

    [Fact]
    public async Task Save_to_empty_after_content_clears_the_lists()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Points = 50,
            Active = { new KharaActiveDocument { KharaId = 1, Counts = { 1 } } },
            Completed = { 3 },
            UpdatedUtc = DateTime.UtcNow,
        });
        await repo.SaveAsync(new KharaStateDocument { Id = id, UpdatedUtc = DateTime.UtcNow });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(0u, got!.Points);
        Assert.Empty(got.Active);
        Assert.Empty(got.Completed);
    }

    [Fact]
    public async Task Characters_are_isolated_from_each_other()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a + 1;
        await repo.SaveAsync(new KharaStateDocument { Id = a, Points = 11, Completed = { 1 }, UpdatedUtc = DateTime.UtcNow });
        await repo.SaveAsync(new KharaStateDocument { Id = b, Points = 22, Completed = { 2 }, UpdatedUtc = DateTime.UtcNow });
        await repo.SaveAsync(new KharaStateDocument { Id = a, Points = 33, Completed = { 3 }, UpdatedUtc = DateTime.UtcNow });

        var gotA = await repo.GetAsync(a);
        var gotB = await repo.GetAsync(b);
        Assert.Equal(33u, gotA!.Points);
        Assert.Equal(new uint[] { 3 }, gotA.Completed);
        Assert.Equal(22u, gotB!.Points);
        Assert.Equal(new uint[] { 2 }, gotB.Completed);
    }

    [Fact]
    public async Task UpdatedUtc_is_normalised_to_utc_and_truncated_to_milliseconds()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        var offset = new DateTimeOffset(2026, 10, 18, 14, 0, 0, TimeSpan.FromHours(2));
        var withTicks = offset.UtcDateTime.AddTicks(4_567); 
        await repo.SaveAsync(new KharaStateDocument { Id = id, UpdatedUtc = withTicks });

        var got = await repo.GetAsync(id);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), got!.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
    }

    [Fact]
    public async Task Unset_UpdatedUtc_round_trips_as_the_minimum()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new KharaStateDocument { Id = id, Points = 1 });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(DateTime.MinValue, got!.UpdatedUtc);
    }

    [Fact]
    public async Task Loaded_document_clone_equals_what_was_saved()
    {
        if (!_fx.Available) return;
        var repo = new KharaStateRepository(_fx.Db!);
        var id = NewCharacterId();
        var original = new KharaStateDocument
        {
            Id = id,
            Points = 12,
            Active = { new KharaActiveDocument { KharaId = 5, State = 2, Counts = { 1, 0, 3 } } },
            Completed = { 4, 8 },
            UpdatedUtc = new DateTime(2026, 5, 5, 5, 5, 5, DateTimeKind.Utc),
        };
        await repo.SaveAsync(original);

        var copy = (await repo.GetAsync(id))!.Clone();
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Points, copy.Points);
        Assert.Equal(original.Active[0].Counts, copy.Active[0].Counts);
        Assert.Equal(original.Completed, copy.Completed);
        Assert.Equal(original.UpdatedUtc, copy.UpdatedUtc);
    }
}
