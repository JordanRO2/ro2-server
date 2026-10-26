using RO2.Domain.Entities;
using RO2.Worlds.Domain;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="WorldEventStateRepository"/> against PostgreSQL: listing, create and replace on save,
/// round-trips of the id, stage and count types, empty and default-date edge cases, and last-write-wins on concurrent saves.
/// </summary>
/// <remarks>
/// Event ids are random and above int.MaxValue so the tests can share one container without colliding.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresWorldEventStateTests
{
    private readonly PostgresFixture _fx;
    public PostgresWorldEventStateTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_200_000_000L);

    
    private static DateTime Ms(int daysAgo) =>
        new DateTime(DateTime.UtcNow.AddDays(-daysAgo).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static WorldEventStateDocument Doc(uint id, byte stage, DateTime entered, DateTime updated, params uint[] counts) => new()
    {
        Id = id,
        Stage = stage,
        StageEnteredUtc = entered,
        Counts = new List<uint>(counts),
        UpdatedUtc = updated,
    };

    private static async Task<WorldEventStateDocument?> Find(WorldEventStateRepository repo, uint id) =>
        (await repo.GetAllAsync()).SingleOrDefault(d => d.Id == id);

    [Fact]
    public async Task Save_then_get_all_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        var entered = Ms(2);
        var updated = Ms(1);
        await repo.SaveAsync(Doc(id, 3, entered, updated, 5, 0, 4_000_000_000u, 7), CancellationToken.None);

        var got = await Find(repo, id);
        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);                                  
        Assert.Equal((byte)3, got.Stage);
        Assert.Equal(entered, got.StageEnteredUtc);
        Assert.Equal(DateTimeKind.Utc, got.StageEnteredUtc.Kind);
        Assert.Equal(updated, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
        Assert.Equal(new uint[] { 5, 0, 4_000_000_000u, 7 }, got.Counts); 
    }

    [Fact]
    public async Task Get_all_excludes_ids_never_saved()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var saved = NewId();
        var unsaved = saved == uint.MaxValue ? saved - 1 : saved + 1;
        await repo.SaveAsync(Doc(saved, 1, Ms(1), Ms(1), 1), CancellationToken.None);

        var all = await repo.GetAllAsync();
        Assert.Contains(all, d => d.Id == saved);
        Assert.DoesNotContain(all, d => d.Id == unsaved);
    }

    [Fact]
    public async Task Get_all_returns_every_saved_event_ordered_by_id()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var a = NewId();
        var b = a + 1;
        var c = a + 2;
        
        await repo.SaveAsync(Doc(c, 3, Ms(3), Ms(3), 30), CancellationToken.None);
        await repo.SaveAsync(Doc(a, 1, Ms(1), Ms(1), 10), CancellationToken.None);
        await repo.SaveAsync(Doc(b, 2, Ms(2), Ms(2), 20), CancellationToken.None);

        var all = await repo.GetAllAsync();
        var ours = all.Where(d => d.Id == a || d.Id == b || d.Id == c).ToList();
        Assert.Equal(new[] { a, b, c }, ours.Select(d => d.Id).ToArray());
        Assert.Equal(new[] { (byte)1, (byte)2, (byte)3 }, ours.Select(d => d.Stage).ToArray());
        Assert.Equal(new uint[] { 10 }, ours[0].Counts);
        Assert.Equal(new uint[] { 20 }, ours[1].Counts);
        Assert.Equal(new uint[] { 30 }, ours[2].Counts);
        
        Assert.Equal(all.Select(d => d.Id).OrderBy(x => x).ToArray(), all.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task Save_replaces_the_whole_state_without_duplicating()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(Doc(id, 1, Ms(5), Ms(5), 1, 2, 3), CancellationToken.None);

        var entered = Ms(1);
        var updated = Ms(0);
        await repo.SaveAsync(Doc(id, 2, entered, updated, 9), CancellationToken.None); 

        var matches = (await repo.GetAllAsync()).Where(d => d.Id == id).ToList();
        var got = Assert.Single(matches);                           
        Assert.Equal((byte)2, got.Stage);
        Assert.Equal(entered, got.StageEnteredUtc);
        Assert.Equal(updated, got.UpdatedUtc);
        Assert.Equal(new uint[] { 9 }, got.Counts);                 
    }

    [Fact]
    public async Task Save_with_empty_counts_round_trips_empty_list()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(Doc(id, 1, Ms(1), Ms(1)), CancellationToken.None);

        var got = await Find(repo, id);
        Assert.NotNull(got);
        Assert.NotNull(got!.Counts);
        Assert.Empty(got.Counts);

        
        await repo.SaveAsync(Doc(id, 1, Ms(1), Ms(1), 4, 5), CancellationToken.None);
        await repo.SaveAsync(Doc(id, 1, Ms(1), Ms(1)), CancellationToken.None);
        Assert.Empty((await Find(repo, id))!.Counts);
    }

    [Fact]
    public async Task Stage_byte_extremes_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var low = NewId();
        var high = low + 1;
        await repo.SaveAsync(Doc(low, byte.MinValue, Ms(1), Ms(1), 1), CancellationToken.None);
        await repo.SaveAsync(Doc(high, byte.MaxValue, Ms(1), Ms(1), 1), CancellationToken.None);

        Assert.Equal(byte.MinValue, (await Find(repo, low))!.Stage);
        Assert.Equal(byte.MaxValue, (await Find(repo, high))!.Stage);
    }

    [Fact]
    public async Task Uint_id_extremes_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        
        await repo.SaveAsync(Doc(uint.MaxValue, 1, Ms(1), Ms(1), 1), CancellationToken.None);
        await repo.SaveAsync(Doc(0, 2, Ms(1), Ms(1), 2), CancellationToken.None);

        var all = await repo.GetAllAsync();
        Assert.Equal((byte)1, all.Single(d => d.Id == uint.MaxValue).Stage);
        Assert.Equal((byte)2, all.Single(d => d.Id == 0).Stage);
    }

    [Fact]
    public async Task Non_utc_dates_are_normalised_to_utc_milliseconds()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        var local = new DateTime(2026, 3, 4, 5, 6, 7, 891, DateTimeKind.Local).AddTicks(1234); 
        await repo.SaveAsync(Doc(id, 1, local, local, 1), CancellationToken.None);

        var got = await Find(repo, id);
        var utc = local.ToUniversalTime();
        var expected = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        Assert.Equal(expected, got!.StageEnteredUtc);               
        Assert.Equal(expected, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.StageEnteredUtc.Kind);
    }

    [Fact]
    public async Task Default_dates_stay_at_minimum_utc()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(new WorldEventStateDocument { Id = id, Stage = 1 }, CancellationToken.None); 

        var got = await Find(repo, id);
        Assert.NotNull(got);
        Assert.Equal(DateTime.MinValue, got!.StageEnteredUtc);
        Assert.Equal(DateTime.MinValue, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.StageEnteredUtc.Kind);
        Assert.Empty(got.Counts);
    }

    [Fact]
    public async Task Concurrent_saves_of_one_event_leave_a_single_consistent_row()
    {
        if (!_fx.Available) return;
        var repo = new WorldEventStateRepository(_fx.Db!);
        var id = NewId();
        var tasks = Enumerable.Range(1, 12)
            .Select(i => repo.SaveAsync(Doc(id, (byte)i, Ms(1), Ms(1), (uint)i, (uint)(i * 10)), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);                                  

        var got = Assert.Single(await repo.GetAllAsync(), d => d.Id == id);
        
        Assert.Equal(new uint[] { got.Stage, (uint)got.Stage * 10 }, got.Counts);
    }
}
