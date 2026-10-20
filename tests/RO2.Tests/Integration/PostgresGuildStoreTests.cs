using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="PostgresGuildRepository"/> (<c>IGuildStore</c>) against a live PostgreSQL
/// instance (<see cref="PostgresFixture"/>). Covers whole-aggregate save and load round-trips (members, ranks,
/// storage, skills, and the byte[] guild GUID), upsert replacement, and delete.
/// </summary>
/// <remarks>Each test uses a fresh random guild id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresGuildStoreTests
{
    private readonly PostgresFixture _fx;
    public PostgresGuildStoreTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)Random.Shared.NextInt64(3_000_000_000L, uint.MaxValue);

    private static Guild Sample(uint id) => new()
    {
        Id = id,
        Guid16 = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        Name = "Knights",
        MasterCharId = 1234,
        Notice = "welcome",
        Gold = 9_000_000_000,            
        Exp = ulong.MaxValue,
        CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Ranks = { new GuildRank { Index = 1, Name = "Officer", Permissions = 7 } },
        Members = { new GuildMember { CharId = 4_000_000_001, Gid = 7, Name = "Alice", Level = 50, AuthId = 1, ContribPoints = 42 } },
        Storage = { new GuildStorageItem { Uid = ulong.MaxValue, ItemId = 501, Count = 3, Refine = 5, StorageSlot = 2, DepositedByCharId = 1234 } },
        Skills = { 101u, 202u },
    };

    private async Task<Guild?> LoadOneAsync(PostgresGuildRepository repo, uint id)
        => (await repo.LoadAllAsync()).FirstOrDefault(g => g.Id == id);

    [Fact]
    public async Task Save_then_load_all_round_trips_the_whole_aggregate()
    {
        if (!_fx.Available) return;
        var repo = new PostgresGuildRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(Sample(id));

        var g = await LoadOneAsync(repo, id);
        Assert.NotNull(g);
        Assert.Equal("Knights", g!.Name);
        Assert.Equal(9_000_000_000, g.Gold);
        Assert.Equal(ulong.MaxValue, g.Exp);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, g.Guid16);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), g.CreatedUtc!.Value.ToUniversalTime());
        Assert.Equal("Alice", Assert.Single(g.Members).Name);
        Assert.Equal(4_000_000_001u, g.Members[0].CharId);
        Assert.Equal(ulong.MaxValue, Assert.Single(g.Storage).Uid);
        Assert.Equal("Officer", Assert.Single(g.Ranks).Name);
        Assert.Equal(new[] { 101u, 202u }, g.Skills);
    }

    [Fact]
    public async Task Save_upserts_the_whole_document()
    {
        if (!_fx.Available) return;
        var repo = new PostgresGuildRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(Sample(id));

        var changed = Sample(id);
        changed.Name = "Renamed";
        changed.Members.Add(new GuildMember { CharId = 5, Name = "Bob" });
        await repo.SaveAsync(changed);

        var g = await LoadOneAsync(repo, id);
        Assert.Equal("Renamed", g!.Name);
        Assert.Equal(2, g.Members.Count);
    }

    [Fact]
    public async Task Delete_removes_the_guild()
    {
        if (!_fx.Available) return;
        var repo = new PostgresGuildRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(Sample(id));
        Assert.NotNull(await LoadOneAsync(repo, id));

        await repo.DeleteAsync(id);
        Assert.Null(await LoadOneAsync(repo, id));
    }
}
