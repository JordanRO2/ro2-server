using Npgsql;

namespace RO2.Persistence.Postgres;

/// <summary>
/// Process-wide PostgreSQL access for the provider. Owns a single pooled <see cref="NpgsqlDataSource"/> built from the
/// configured connection string; the Postgres counterpart of <c>MongoDbContext</c>. Repositories open a short-lived
/// pooled connection per operation and dispose it.
/// </summary>
/// <remarks>
/// Like the Mongo context, this performs no I/O in its constructor; building the data source does not open a socket.
/// Schema creation lives in <see cref="PostgresSchema"/> and is run off the boot path by the provider selector, never
/// from here.
/// </remarks>
public sealed class PostgresDbContext : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDbContext(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

 /// <summary>Open a pooled connection for one operation; the caller disposes it (await using).</summary>
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        => _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
