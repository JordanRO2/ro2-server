using Npgsql;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure.Postgres;
using RO2.Tests.Integration;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// S-H9 against a live PostgreSQL (the shared Testcontainers fixture; soft-skips without Docker): the
/// <c>lower(username)</c> unique index, the registration race, and targeted column updates. Every test uses
/// fresh random usernames.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresAccountHardeningTests
{
    private readonly PostgresFixture _fx;

    public PostgresAccountHardeningTests(PostgresFixture fx) => _fx = fx;

    private PostgresAccountRepository NewRepo() => new(new IdentityPostgresContext(_fx.ConnectionString!));

    private static string NewUsername() => "h_" + Guid.NewGuid().ToString("N")[..20]; // the Credentials cap is 36 characters, scenarios append up to 4

    [Fact]
    public async Task Ensure_indexes_creates_the_case_insensitive_unique_index()
    {
        if (!_fx.Available) return;
        var repo = NewRepo();

        Assert.True(await repo.EnsureIndexesAsync());
        Assert.True(repo.UsernameUniquenessEnforced);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'accounts' AND indexname = @n", conn);
        cmd.Parameters.AddWithValue("n", PostgresAccountRepository.UsernameIndexName);
        var def = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(def);
        Assert.Contains("UNIQUE", def);
        Assert.Contains("lower(username)", def);
    }

    [Fact]
    public async Task A_case_variant_username_is_the_same_account()
    {
        if (!_fx.Available) return;
        await AccountStoreHardeningScenarios.CaseVariantUsernameIsTheSameAccount(NewRepo(), NewUsername());
    }

    [Fact]
    public async Task Concurrent_SetSecondPassword_and_Authenticate_both_persist()
    {
        if (!_fx.Available) return;
        await AccountStoreHardeningScenarios.SetSecondPasswordAndAuthenticateBothPersist(NewRepo(), NewUsername());
    }
}
