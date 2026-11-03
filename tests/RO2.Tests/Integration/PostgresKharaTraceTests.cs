using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="KharaTraceRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the empty read, order-preserving round trips, full-replace upsert, per-character isolation and concurrent first saves. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresKharaTraceTests
{
    private readonly PostgresFixture _fx;
    public PostgresKharaTraceTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(1, 2_000_000_000);

    [DockerFact]
    public async Task Get_never_saved_is_empty()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        Assert.Empty(await repo.GetAsync(NewCharacterId()));
    }

    [DockerFact]
    public async Task Save_then_get_round_trips_order_and_duplicates()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = NewCharacterId();
        var list = new uint[] { 30, 5, 5, 4_000_000_000, 7, 30 }; 
        await repo.SaveAsync(cid, list);
        Assert.Equal(list, await repo.GetAsync(cid));
    }

    [DockerFact]
    public async Task Save_replaces_whole_list_not_merge()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = NewCharacterId();
        await repo.SaveAsync(cid, new uint[] { 1, 2, 3 });
        await repo.SaveAsync(cid, new uint[] { 9, 1 });
        Assert.Equal(new uint[] { 9, 1 }, await repo.GetAsync(cid));
    }

    [DockerFact]
    public async Task Save_empty_list_clears_previous()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = NewCharacterId();
        await repo.SaveAsync(cid, new uint[] { 11, 22 });
        await repo.SaveAsync(cid, Array.Empty<uint>());
        Assert.Empty(await repo.GetAsync(cid));
    }

    [DockerFact]
    public async Task Save_empty_list_on_first_save_is_empty()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = NewCharacterId();
        await repo.SaveAsync(cid, new List<uint>());
        Assert.Empty(await repo.GetAsync(cid));
    }

    [DockerFact]
    public async Task Characters_are_isolated()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a + 1;
        await repo.SaveAsync(a, new uint[] { 100, 200 });
        await repo.SaveAsync(b, new uint[] { 300 });
        Assert.Equal(new uint[] { 100, 200 }, await repo.GetAsync(a));
        Assert.Equal(new uint[] { 300 }, await repo.GetAsync(b));
    }

    [DockerFact]
    public async Task Character_id_above_int_max_round_trips()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = (uint)Random.Shared.NextInt64(3_000_000_000, 4_000_000_000);
        await repo.SaveAsync(cid, new uint[] { 42 });
        Assert.Equal(new uint[] { 42 }, await repo.GetAsync(cid));
    }

    [DockerFact]
    public async Task Concurrent_first_saves_do_not_conflict_and_last_writer_wins_with_a_valid_list()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        var repo = new KharaTraceRepository(_fx.Db!);
        var cid = NewCharacterId();
        var lists = Enumerable.Range(1, 8).Select(i => new uint[] { (uint)i, (uint)i * 10 }).ToArray();
        await Task.WhenAll(lists.Select(l => repo.SaveAsync(cid, l))); 
        var stored = await repo.GetAsync(cid);
        Assert.Contains(lists, l => l.SequenceEqual(stored)); 
    }
}
