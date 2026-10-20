using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="ZoneAuthTokenRepository"/> against PostgreSQL: one-time consumption, expiry window,
/// unknown tokens and upsert on issue. Each test uses a fresh token so the tests can share one container.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresZoneAuthTokenTests
{
    private readonly PostgresFixture _fx;
    public PostgresZoneAuthTokenTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Consume_returns_account_then_null_one_time()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var token = Guid.NewGuid();
        
        await repo.IssueAsync(token, 4_000_000_000, TimeSpan.FromMinutes(1));
        Assert.Equal(4_000_000_000u, await repo.ConsumeAsync(token));
        Assert.Null(await repo.ConsumeAsync(token)); 
    }

    [Fact]
    public async Task Consume_unknown_token_is_null()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        Assert.Null(await repo.ConsumeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Expired_token_is_null()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var token = Guid.NewGuid();
        await repo.IssueAsync(token, 123, TimeSpan.FromSeconds(-1)); 
        Assert.Null(await repo.ConsumeAsync(token));
    }

    [Fact]
    public async Task Issue_upserts_latest_account()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var token = Guid.NewGuid();
        await repo.IssueAsync(token, 111, TimeSpan.FromMinutes(1));
        await repo.IssueAsync(token, 222, TimeSpan.FromMinutes(1)); 
        Assert.Equal(222u, await repo.ConsumeAsync(token));
    }
}
