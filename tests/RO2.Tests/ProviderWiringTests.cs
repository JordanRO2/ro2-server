using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Configuration;
using RO2.Application.Interfaces;
using RO2.Infrastructure;
using Xunit;

namespace RO2.Tests;

/// <summary>
/// Verifies that the persistence composition root registers the implementation selected by
/// <see cref="DatabaseConfig.Provider"/>. It inspects only the registered <see cref="ServiceDescriptor"/>s:
/// no container is built and no database is contacted, since AddPersistence creates a lazy Mongo or Npgsql
/// context that never connects.
/// </summary>
public sealed class ProviderWiringTests
{
    private static string? ImplOf<T>(IServiceCollection s)
        => s.LastOrDefault(d => d.ServiceType == typeof(T))?.ImplementationType?.FullName;

    [Fact]
    public void Postgres_provider_selects_postgres_implementations()
    {
        var s = new ServiceCollection();
        s.AddPersistence(new DatabaseConfig { Provider = "Postgres", ConnectionString = "Host=localhost;Database=x" });

        Assert.Equal("RO2.Persistence.Postgres.Repositories.CharacterRepository", ImplOf<ICharacterRepository>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.InventoryRepository", ImplOf<IInventoryRepository>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.TimedEnchantRepository", ImplOf<ITimedEnchantRepository>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.ChannelPresenceStore", ImplOf<IChannelPresenceStore>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.OnlineAccountRegistry", ImplOf<IOnlineAccountRegistry>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.AccountAccessRepository", ImplOf<IAccountAccessRepository>(s));
        Assert.Equal("RO2.Persistence.Postgres.Repositories.OneShotTokenStore", ImplOf<IOneShotTokenStore>(s));
        
        Assert.Equal("RO2.Persistence.Redis.RedisCloseRangeRoomSignalBus", ImplOf<ICloseRangeRoomSignals>(s));
    }

    [Fact]
    public void Mongo_provider_selects_mongo_implementations()
    {
        var s = new ServiceCollection();
        s.AddPersistence(new DatabaseConfig { Provider = "Mongo", ConnectionString = "mongodb://localhost", DatabaseName = "x" });

        Assert.Equal("RO2.Persistence.Mongo.Repositories.CharacterRepository", ImplOf<ICharacterRepository>(s));
        Assert.Equal("RO2.Persistence.Mongo.Repositories.InventoryRepository", ImplOf<IInventoryRepository>(s));
        Assert.Equal("RO2.Persistence.Mongo.Persistence.ChannelPresenceStore", ImplOf<IChannelPresenceStore>(s));
        Assert.Equal("RO2.Persistence.Mongo.Repositories.OnlineAccountRegistry", ImplOf<IOnlineAccountRegistry>(s));
        Assert.Equal("RO2.Persistence.Mongo.Persistence.OneShotTokenStore", ImplOf<IOneShotTokenStore>(s));
        Assert.Equal("RO2.Persistence.Mongo.Persistence.CloseRangeRoomSignalBus", ImplOf<ICloseRangeRoomSignals>(s));
    }

    [Fact]
    public void Unknown_provider_throws()
        => Assert.Throws<NotSupportedException>(
            () => new ServiceCollection().AddPersistence(new DatabaseConfig { Provider = "Oracle" }));
}
