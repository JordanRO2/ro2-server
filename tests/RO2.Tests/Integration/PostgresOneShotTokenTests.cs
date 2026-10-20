using Npgsql;
using RO2.Application.Interfaces;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="OneShotTokenStore"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers one-time consume, expiry, purpose and account checks, duplicate-issue rejection and integer round-trips. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresOneShotTokenTests
{
    private readonly PostgresFixture _fx;
    public PostgresOneShotTokenTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Consume_returns_identity_then_null_one_time()
    {
        if (!_fx.Available) return;
        var store = new OneShotTokenStore(_fx.Db!);
        var token = OneShotTokens.NewToken();
        Assert.True(await store.IssueAsync(token, OneShotTokens.PurposeMessenger, 4_000_000_000u, ulong.MaxValue, TimeSpan.FromMinutes(5)));

        var got = await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger, 4_000_000_000u);
        Assert.NotNull(got);
        Assert.Equal(4_000_000_000u, got!.AccountId);
        Assert.Equal(ulong.MaxValue, got.CharacterUid);
        Assert.Equal(OneShotTokens.PurposeMessenger, got.Purpose);

        Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger, 4_000_000_000u)); 
    }

    [Fact]
    public async Task Unknown_token_is_null()
    {
        if (!_fx.Available) return;
        var store = new OneShotTokenStore(_fx.Db!);
        Assert.Null(await store.ConsumeAsync(OneShotTokens.NewToken(), OneShotTokens.PurposeZone));
    }

    [Fact]
    public async Task Wrong_purpose_or_account_does_not_burn_the_token()
    {
        if (!_fx.Available) return;
        var store = new OneShotTokenStore(_fx.Db!);
        var token = OneShotTokens.NewToken();
        await store.IssueAsync(token, OneShotTokens.PurposeZone, 5u, 6ul, TimeSpan.FromMinutes(5));

        Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger));      
        Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeZone, 999u));     
        Assert.NotNull(await store.ConsumeAsync(token, OneShotTokens.PurposeZone, 5u));    
    }

    [Fact]
    public async Task Expired_token_is_null()
    {
        if (!_fx.Available) return;
        var store = new OneShotTokenStore(_fx.Db!);
        var token = OneShotTokens.NewToken();
        
        await using (var conn = await _fx.Db!.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO one_shot_tokens (token, purpose, account_id, character_uid, issued_utc, expiry) " +
            "VALUES (@t, @p, 1, 1, now() - interval '10 minutes', now() - interval '1 minute')", conn))
        {
            cmd.Parameters.AddWithValue("t", token);
            cmd.Parameters.AddWithValue("p", OneShotTokens.PurposeZone);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeZone));
    }

    [Fact]
    public async Task Duplicate_issue_returns_false()
    {
        if (!_fx.Available) return;
        var store = new OneShotTokenStore(_fx.Db!);
        var token = OneShotTokens.NewToken();
        Assert.True(await store.IssueAsync(token, OneShotTokens.PurposeZone, 1u, 2ul, TimeSpan.FromMinutes(5)));
        Assert.False(await store.IssueAsync(token, OneShotTokens.PurposeZone, 1u, 2ul, TimeSpan.FromMinutes(5)));
    }
}
