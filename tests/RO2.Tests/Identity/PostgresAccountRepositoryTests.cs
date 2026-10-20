using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure.Postgres;
using RO2.Tests.Integration;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// Live-PostgreSQL tests for <see cref="PostgresAccountRepository"/> against the shared Testcontainers
/// database (soft-skips when Docker is unavailable). Every test uses fresh random usernames and ids.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresAccountRepositoryTests
{
    private readonly PostgresFixture _fx;

    public PostgresAccountRepositoryTests(PostgresFixture fx) => _fx = fx;

    private PostgresAccountRepository NewRepo() =>
        new(new IdentityPostgresContext(_fx.ConnectionString!));

    private static string NewUsername() => "u_" + Guid.NewGuid().ToString("N");

    // Random id above int.MaxValue so tests never collide with sequence-issued ids and exercise the uint range.
    private static AccountId NewHighId() => new((uint)Random.Shared.NextInt64(int.MaxValue + 1L, uint.MaxValue));

    // A UTC instant offset from now, truncated to whole seconds (timestamptz keeps microseconds only).
    private static DateTime Utc(int secondsFromNow)
    {
        var t = DateTime.UtcNow.AddSeconds(secondsFromNow);
        return new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    [Fact]
    public async Task NextId_returns_strictly_increasing_unique_ids()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();

        var ids = new List<uint>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add((await repo.NextIdAsync()).Value);
        }

        Assert.Equal(20, ids.Distinct().Count());
        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] > ids[i - 1]);
        }
    }

    [Fact]
    public async Task NextId_is_unique_under_concurrency()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();

        var ids = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ => (await repo.NextIdAsync()).Value));

        Assert.Equal(30, ids.Distinct().Count());
    }

    [Fact]
    public async Task Add_then_get_round_trips_every_field_for_a_banned_account()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();
        var id = NewHighId();
        var username = NewUsername();
        var created = Utc(-3600);
        var lastLogin = Utc(-60);
        var banExpires = Utc(86400);
        var account = Account.Rehydrate(
            id, username, "hash-main", "hash-pin", Email.FromStorage("a@b.example"),
            new BanPolicy(true, banExpires, "cheating"), 7, created, lastLogin);

        await repo.AddAsync(account);

        var byId = await repo.GetByIdAsync(id);
        var byName = await repo.GetByUsernameAsync(username);
        foreach (var loaded in new[] { byId, byName })
        {
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded.Id);
            Assert.True(loaded.Id.Value > int.MaxValue);
            Assert.Equal(username, loaded.Username);
            Assert.Equal("hash-main", loaded.PasswordHash);
            Assert.Equal("hash-pin", loaded.SecondPasswordHash);
            Assert.Equal("a@b.example", loaded.Email.Value);
            Assert.True(loaded.Ban.IsBanned);
            Assert.Equal(banExpires, loaded.Ban.BanExpires);
            Assert.Equal(DateTimeKind.Utc, loaded.Ban.BanExpires!.Value.Kind);
            Assert.Equal("cheating", loaded.Ban.BanReason);
            Assert.Equal((byte)7, loaded.AccessLevel);
            Assert.Equal(created, loaded.CreatedAt);
            Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
            Assert.Equal(lastLogin, loaded.LastLogin);
        }
    }

    [Fact]
    public async Task Add_then_get_round_trips_nulls_and_empty_email_for_a_plain_account()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();
        var id = NewHighId();
        var created = Utc(-10);
        await repo.AddAsync(Account.Rehydrate(
            id, NewUsername(), "hash-main", null, Email.None, BanPolicy.NotBanned, 0, created, null));

        var loaded = await repo.GetByIdAsync(id);

        Assert.NotNull(loaded);
        Assert.Null(loaded.SecondPasswordHash);
        Assert.False(loaded.HasSecondPassword);
        Assert.True(loaded.Email.IsNone);
        Assert.False(loaded.Ban.IsBanned);
        Assert.Null(loaded.Ban.BanExpires);
        Assert.Null(loaded.Ban.BanReason);
        Assert.Equal((byte)0, loaded.AccessLevel);
        Assert.Null(loaded.LastLogin);
        Assert.Equal(created, loaded.CreatedAt);
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id_and_username()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();

        Assert.Null(await repo.GetByIdAsync(NewHighId()));
        Assert.Null(await repo.GetByUsernameAsync(NewUsername()));
    }

    [Fact]
    public async Task UsernameExists_reflects_presence()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();
        var username = NewUsername();

        Assert.False(await repo.UsernameExistsAsync(username));

        await repo.AddAsync(Account.Rehydrate(
            NewHighId(), username, "h", null, Email.None, BanPolicy.NotBanned, 0, Utc(-5), null));

        Assert.True(await repo.UsernameExistsAsync(username));
        Assert.False(await repo.UsernameExistsAsync(NewUsername()));
    }

    [Fact]
    public async Task Update_changes_the_mutable_fields()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();
        var id = NewHighId();
        var username = NewUsername();
        var created = Utc(-500);
        await repo.AddAsync(Account.Rehydrate(
            id, username, "old-hash", null, Email.None, BanPolicy.NotBanned, 0, created, null));

        var lastLogin = Utc(-1);
        var banExpires = Utc(3600);
        await repo.UpdateAsync(Account.Rehydrate(
            id, username, "new-hash", "new-pin", Email.FromStorage("x@y.example"),
            new BanPolicy(true, banExpires, "spam"), 3, created, lastLogin));

        var loaded = await repo.GetByIdAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal("new-hash", loaded.PasswordHash);
        Assert.Equal("new-pin", loaded.SecondPasswordHash);
        Assert.Equal("x@y.example", loaded.Email.Value);
        Assert.True(loaded.Ban.IsBanned);
        Assert.Equal(banExpires, loaded.Ban.BanExpires);
        Assert.Equal("spam", loaded.Ban.BanReason);
        Assert.Equal((byte)3, loaded.AccessLevel);
        Assert.Equal(lastLogin, loaded.LastLogin);
        Assert.Equal(created, loaded.CreatedAt);
    }

    [Fact]
    public async Task Adding_a_second_account_with_the_same_username_is_rejected_as_a_DomainException()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();
        var username = NewUsername();
        await repo.AddAsync(Account.Rehydrate(
            NewHighId(), username, "h", null, Email.None, BanPolicy.NotBanned, 0, Utc(-5), null));

        // The UNIQUE (username) violation surfaces as the domain's refusal, not an uncaught PostgresException (S-H9).
        await Assert.ThrowsAsync<DomainException>(() => repo.AddAsync(Account.Rehydrate(
            NewHighId(), username, "h2", null, Email.None, BanPolicy.NotBanned, 0, Utc(-5), null)));
    }
}
