using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="GameMallListRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers newest-first ordering, skipping known ids, the in-batch duplicate
/// rule, no-op adds, per-account isolation, and concurrent adds.
/// </summary>
/// <remarks>Each test uses a fresh random account id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresGameMallListTests
{
    private readonly PostgresFixture _fx;
    public PostgresGameMallListTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewAccount() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [Fact]
    public async Task Get_unknown_account_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        Assert.Empty(await repo.GetAsync(NewAccount()));
    }

    [Fact]
    public async Task Add_to_empty_stores_ids_newest_first()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        
        var stored = await repo.AddAsync(acct, new uint[] { 1, 2, 3 });
        Assert.Equal(new uint[] { 3, 2, 1 }, stored);
        Assert.Equal(new uint[] { 3, 2, 1 }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Add_puts_new_ids_in_front_of_existing_ones()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        await repo.AddAsync(acct, new uint[] { 10, 20 });
        var stored = await repo.AddAsync(acct, new uint[] { 30 });
        Assert.Equal(new uint[] { 30, 20, 10 }, stored);
        Assert.Equal(new uint[] { 30, 20, 10 }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Add_skips_ids_already_in_the_list()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        await repo.AddAsync(acct, new uint[] { 1, 2 });        
        var stored = await repo.AddAsync(acct, new uint[] { 2, 3 }); 
        Assert.Equal(new uint[] { 3, 2, 1 }, stored);
        Assert.Equal(new uint[] { 3, 2, 1 }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Add_skips_a_repeat_inside_one_batch()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        var stored = await repo.AddAsync(acct, new uint[] { 5, 5, 6 });
        Assert.Equal(new uint[] { 6, 5 }, stored);
    }

    [Fact]
    public async Task Add_of_only_known_ids_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        await repo.AddAsync(acct, new uint[] { 7, 8, 9 });     
        var stored = await repo.AddAsync(acct, new uint[] { 8, 9, 7 });
        Assert.Equal(new uint[] { 9, 8, 7 }, stored);
        Assert.Equal(new uint[] { 9, 8, 7 }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Add_of_no_ids_returns_the_current_list_and_empty_for_a_new_account()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var fresh = NewAccount();
        Assert.Empty(await repo.AddAsync(fresh, Array.Empty<uint>()));
        Assert.Empty(await repo.GetAsync(fresh));

        var acct = NewAccount();
        await repo.AddAsync(acct, new uint[] { 4 });
        Assert.Equal(new uint[] { 4 }, await repo.AddAsync(acct, Array.Empty<uint>()));
    }

    [Fact]
    public async Task Lists_are_isolated_per_account()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var a = NewAccount();
        var b = NewAccount();
        await repo.AddAsync(a, new uint[] { 1, 2 });
        await repo.AddAsync(b, new uint[] { 2, 9 });           
        Assert.Equal(new uint[] { 2, 1 }, await repo.GetAsync(a));
        Assert.Equal(new uint[] { 9, 2 }, await repo.GetAsync(b));
    }

    [Fact]
    public async Task Large_uint_ids_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = 4_000_000_000u + (uint)Random.Shared.Next(0, 200_000_000);
        var stored = await repo.AddAsync(acct, new uint[] { 4_000_000_001u, uint.MaxValue });
        Assert.Equal(new uint[] { uint.MaxValue, 4_000_000_001u }, stored);
        Assert.Equal(new uint[] { uint.MaxValue, 4_000_000_001u }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Concurrent_adds_of_the_same_id_store_it_once()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => repo.AddAsync(acct, new uint[] { 77 }))));
        Assert.All(results, r => Assert.Equal(new uint[] { 77 }, r));
        Assert.Equal(new uint[] { 77 }, await repo.GetAsync(acct));
    }

    [Fact]
    public async Task Concurrent_adds_of_different_ids_lose_none()
    {
        if (!_fx.Available) return;
        var repo = new GameMallListRepository(_fx.Db!);
        var acct = NewAccount();
        await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(i => Task.Run(() => repo.AddAsync(acct, new[] { (uint)i }))));
        var stored = await repo.GetAsync(acct);
        Assert.Equal(10, stored.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i).Order(), stored.Order());
    }
}
