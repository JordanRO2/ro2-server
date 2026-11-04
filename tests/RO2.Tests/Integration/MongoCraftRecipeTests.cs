using RO2.Economy.Infrastructure.Mongo;
using RO2.Persistence.Mongo.Persistence;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// The Mongo twin of <see cref="PostgresCraftRecipeTests"/>' repeat/concurrent cases, against a REAL mongo:7 container: a
/// recipe already learned reports false (so a repeated or concurrent 1348 is not charged twice), exactly as the
/// Postgres <c>ON CONFLICT DO NOTHING</c> does. Reports SKIPPED without Docker.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoCraftRecipeTests
{
    private readonly MongoMailFixture _fx;
    public MongoCraftRecipeTests(MongoMailFixture fx) => _fx = fx;

    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(1_000_000, 1_000_000_000);

    private CraftRecipeRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new CraftRecipeRepository(new MongoDbContext(_fx.ConnectionString, "ro2_it_craft_recipes"));
    }

    [DockerFact]
    public async Task Add_new_recipe_true_then_repeated_recipe_false_and_other_recipe_true()
    {
        var repo = Repo();
        var ch = NewCharacterId();
        Assert.True(await repo.AddAsync(ch, 77));
        Assert.False(await repo.AddAsync(ch, 77));
        Assert.True(await repo.AddAsync(ch, 78));
        Assert.False(await repo.AddAsync(ch, 78));
        Assert.Equal(new ushort[] { 77, 78 }, (await repo.GetAsync(ch)).ToArray());
    }

    [DockerFact]
    public async Task Concurrent_learn_of_the_same_recipe_succeeds_exactly_once()
    {
        var repo = Repo();
        var ch = NewCharacterId();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => repo.AddAsync(ch, 4321))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(new ushort[] { 4321 }, (await repo.GetAsync(ch)).ToArray());
    }
}
