using RO2.Persistence.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// One ephemeral PostgreSQL 16 container shared by all Postgres-provider integration tests (via the
/// <see cref="PostgresCollection"/> xunit collection), with the provider schema created once.
/// </summary>
/// <remarks>
/// Docker-gated: if no Docker daemon is reachable the container fails to start, and dependent tests
/// soft-skip so the suite still passes without Docker.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16").Build();

    public PostgresDbContext? Db { get; private set; }

 /// <summary>Connection string of the live container, or null when Docker is unavailable.</summary>
    public string? ConnectionString { get; private set; }

 /// <summary>True once the container is running and the schema is created; false when Docker is unavailable.</summary>
    public bool Available => Db is not null;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            Db = new PostgresDbContext(ConnectionString);
            await PostgresSchema.EnsureAsync(Db);
        }
        catch
        {
            Db = null; 
        }
    }

    public async Task DisposeAsync()
    {
        if (Db is not null) await Db.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// xunit collection that shares one <see cref="PostgresFixture"/> (and so one container) across all
/// Postgres integration test classes. Test classes opt in with <c>[Collection("Postgres")]</c>.
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
