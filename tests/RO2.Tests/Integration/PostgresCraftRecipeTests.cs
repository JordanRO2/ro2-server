using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CraftRecipeRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers empty reads, first and repeated learns, learn order, per-character
/// isolation, unsigned-range round-trips, and a concurrent learn of the same recipe.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresCraftRecipeTests
{
    private readonly PostgresFixture _fx;
    public PostgresCraftRecipeTests(PostgresFixture fx) => _fx = fx;

    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(1_000_000, uint.MaxValue);

    [Fact]
    public async Task Get_unknown_character_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        Assert.Empty(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Add_new_recipe_returns_true_and_is_stored()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var ch = NewCharacterId();
        Assert.True(await repo.AddAsync(ch, 1234));
        Assert.Equal(new ushort[] { 1234 }, (await repo.GetAsync(ch)).ToArray());
    }

    [Fact]
    public async Task Add_repeated_recipe_returns_false_and_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var ch = NewCharacterId();
        Assert.True(await repo.AddAsync(ch, 77));
        Assert.False(await repo.AddAsync(ch, 77));
        Assert.False(await repo.AddAsync(ch, 77));
        Assert.Equal(new ushort[] { 77 }, (await repo.GetAsync(ch)).ToArray());
    }

    [Fact]
    public async Task Get_returns_recipes_in_learn_order()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var ch = NewCharacterId();
        
        foreach (ushort r in new ushort[] { 900, 5, 300, 42 })
            Assert.True(await repo.AddAsync(ch, r));
        Assert.False(await repo.AddAsync(ch, 5)); 
        Assert.Equal(new ushort[] { 900, 5, 300, 42 }, (await repo.GetAsync(ch)).ToArray());
    }

    [Fact]
    public async Task Recipes_are_isolated_per_character()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        Assert.True(await repo.AddAsync(a, 10));
        Assert.True(await repo.AddAsync(b, 10)); 
        Assert.True(await repo.AddAsync(b, 11));
        Assert.Equal(new ushort[] { 10 }, (await repo.GetAsync(a)).ToArray());
        Assert.Equal(new ushort[] { 10, 11 }, (await repo.GetAsync(b)).ToArray());
    }

    [Fact]
    public async Task Unsigned_extremes_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        
        var ch = (uint)Random.Shared.NextInt64(3_000_000_000, uint.MaxValue);
        Assert.True(await repo.AddAsync(ch, ushort.MaxValue));
        Assert.True(await repo.AddAsync(ch, 0));
        Assert.False(await repo.AddAsync(ch, ushort.MaxValue));
        Assert.Equal(new ushort[] { ushort.MaxValue, 0 }, (await repo.GetAsync(ch)).ToArray());
    }

    [Fact]
    public async Task Concurrent_add_of_same_recipe_has_exactly_one_winner()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var ch = NewCharacterId();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => repo.AddAsync(ch, 555)));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(15, results.Count(r => !r));
        Assert.Equal(new ushort[] { 555 }, (await repo.GetAsync(ch)).ToArray());
    }

    [Fact]
    public async Task Concurrent_add_of_distinct_recipes_stores_all()
    {
        if (!_fx.Available) return;
        var repo = new CraftRecipeRepository(_fx.Db!);
        var ch = NewCharacterId();
        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => repo.AddAsync(ch, (ushort)(1000 + i))));
        Assert.All(results, Assert.True);
        var stored = (await repo.GetAsync(ch)).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (ushort)(1000 + i)).ToArray(), stored);
    }
}
