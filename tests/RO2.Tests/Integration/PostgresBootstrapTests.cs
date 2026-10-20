using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RO2.Application.Configuration;
using RO2.Infrastructure;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Verifies that a Postgres-provider host can start its store on its own: <c>AddPersistence</c> followed by
/// <c>EnsurePersistenceReadyAsync</c> creates the schema without an external migration step.
/// </summary>
/// <remarks>Requires Docker; see <see cref="PostgresFixture"/>.</remarks>
[Collection("Postgres")]
public sealed class PostgresBootstrapTests
{
    private readonly PostgresFixture _fx;
    public PostgresBootstrapTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task EnsurePersistenceReady_creates_the_schema_on_postgres()
    {
        if (!_fx.Available) return;
        var config = new DatabaseConfig { Provider = "Postgres", ConnectionString = _fx.ConnectionString! };
        await using var provider = new ServiceCollection()
            .AddPersistence(config)
            .BuildServiceProvider();

        await provider.EnsurePersistenceReadyAsync(config, NullLogger.Instance);

        
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT to_regclass('public.accounts') IS NOT NULL AND to_regclass('public.characters') IS NOT NULL", conn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }
}
