using StackExchange.Redis;

namespace RO2.Persistence.Redis;

/// <summary>
/// Process-wide Redis access: a single lazily opened <see cref="ConnectionMultiplexer"/> built from the configured
/// connection string. The constructor performs no I/O; the connection opens on first use of <see cref="Db"/>.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _mux;

    public RedisConnection(string connectionString)
        => _mux = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(connectionString));

 /// <summary>The default database. Opens the connection on first use.</summary>
    public IDatabase Db => _mux.Value.GetDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_mux.IsValueCreated) await _mux.Value.DisposeAsync();
    }
}
