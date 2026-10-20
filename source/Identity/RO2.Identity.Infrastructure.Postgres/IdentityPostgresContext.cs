using Npgsql;

namespace RO2.Identity.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL context for the Identity bounded context. Provides a lazily built, pooled
/// <see cref="NpgsqlDataSource"/> created from the connection string.
/// </summary>
/// <remarks>
/// The constructor performs no I/O. Each repository operation opens a short-lived pooled connection. The
/// <c>accounts</c> table and its id sequence are created by the schema setup, not by this class.
/// </remarks>
public sealed class IdentityPostgresContext : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

 /// <summary>Creates the pooled data source. No connection is opened until first use.</summary>
    public IdentityPostgresContext(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

 /// <summary>Opens a pooled connection for one operation. The caller disposes it.</summary>
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        => _dataSource.OpenConnectionAsync(ct);

 /// <summary>Disposes the underlying data source and its pooled connections.</summary>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
