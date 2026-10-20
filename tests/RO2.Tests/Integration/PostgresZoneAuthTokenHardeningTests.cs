using Npgsql;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// S-H8, store side, on the Postgres provider: issuing a token supersedes the account's older ones (one live
/// token per account) and prunes expired rows, the stand-in for Mongo's TTL index. Fresh GUIDs and random
/// account ids per test so they share the container.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresZoneAuthTokenHardeningTests
{
    private readonly PostgresFixture _fx;
    public PostgresZoneAuthTokenHardeningTests(PostgresFixture fx) => _fx = fx;

    private static uint NewAccountId() => (uint)Random.Shared.NextInt64(1_000_000, int.MaxValue);

    private async Task<long> CountRowsAsync(string whereClause, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM zone_auth_tokens WHERE {whereClause}", conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Issuing_twice_for_one_account_leaves_one_live_token()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var account = NewAccountId();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await repo.IssueAsync(first, account, TimeSpan.FromMinutes(5));
        await Task.Delay(20); // the later issue must carry the later expiry
        await repo.IssueAsync(second, account, TimeSpan.FromMinutes(5));

        Assert.Equal(1, await CountRowsAsync("account_id = @a", ("a", (long)account)));
        Assert.Null(await repo.ConsumeAsync(first));
        Assert.Equal(account, await repo.ConsumeAsync(second));
    }

    [Fact]
    public async Task Issuing_for_one_account_leaves_another_accounts_token_alone()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var accountA = NewAccountId();
        var accountB = accountA + 1;
        var tokenA = Guid.NewGuid();

        await repo.IssueAsync(tokenA, accountA, TimeSpan.FromMinutes(5));
        await repo.IssueAsync(Guid.NewGuid(), accountB, TimeSpan.FromMinutes(5));

        Assert.Equal(accountA, await repo.ConsumeAsync(tokenA));
    }

    [Fact]
    public async Task Issue_prunes_expired_rows()
    {
        if (!_fx.Available) return;
        var repo = new ZoneAuthTokenRepository(_fx.Db!);
        var staleAccount = NewAccountId();
        var staleToken = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO zone_auth_tokens (token, account_id, expires_at) VALUES (@t, @a, @e)", conn);
            insert.Parameters.AddWithValue("t", staleToken);
            insert.Parameters.AddWithValue("a", (long)staleAccount);
            insert.Parameters.AddWithValue("e", DateTime.UtcNow.AddHours(-1));
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await CountRowsAsync("token = @t", ("t", staleToken)));

        await repo.IssueAsync(Guid.NewGuid(), NewAccountId() + 5, TimeSpan.FromMinutes(5)); // an unrelated issue

        Assert.Equal(0, await CountRowsAsync("token = @t", ("t", staleToken)));
    }
}
