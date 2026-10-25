using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Configuration;
using RO2.Application.Interfaces;
using RO2.Inventory.Domain;
using MongoRepos = RO2.Persistence.Mongo.Repositories;
using MongoPersist = RO2.Persistence.Mongo.Persistence;
using PgRepos = RO2.Persistence.Postgres.Repositories;

namespace RO2.Infrastructure;

/// <summary>
/// Composition root for persistence: the one place that turns <see cref="DatabaseConfig.Provider"/> into a
/// database context and the repositories built on it. Two providers exist: "Mongo" (the default, complete)
/// and "Postgres" (RO2.Persistence.Postgres). Each aggregate that has been ported to both is registered
/// through <see cref="AddProviderRepository{TService,TMongo,TPostgres}"/>, which selects the implementation
/// by provider while keeping the Mongo lifetime, so the Mongo path is byte-for-byte what it was.
/// </summary>
/// <remarks>
/// Not yet ported to Postgres, so still Mongo-only: the close-range signal bus (its target is Redis /
/// LISTEN-NOTIFY, not a table) and the one-shot token store (no interface yet). A server configured for
/// Postgres therefore cannot resolve those two, and the Identity account store is a separate bounded
/// context with its own provider — Postgres is not fully bootable until those land. See
/// docs/architecture/DATA-STORE-ROADMAP.md.
/// </remarks>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, DatabaseConfig config)
    {
        var provider = config.Provider;
        switch (provider)
        {
            case "Mongo":
                services.AddSingleton(new MongoPersist.MongoDbContext(config.ConnectionString, config.DatabaseName));
                
                
                services.AddSingleton<ICloseRangeRoomSignals, MongoPersist.CloseRangeRoomSignalBus>();
                break;
            case "Postgres":
                
                services.AddSingleton(new RO2.Persistence.Postgres.PostgresDbContext(config.ConnectionString));
                
                
                services.AddSingleton(new RO2.Persistence.Redis.RedisConnection(config.RedisConnectionString));
                services.AddSingleton<ICloseRangeRoomSignals, RO2.Persistence.Redis.RedisCloseRangeRoomSignalBus>();
                break;
            default:
                throw new NotSupportedException($"Unknown Database:Provider '{config.Provider}'");
        }

        
        services.AddProviderRepository<ICharacterRepository, MongoRepos.CharacterRepository, PgRepos.CharacterRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<IInventoryRepository, MongoRepos.InventoryRepository, PgRepos.InventoryRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<ITimedEnchantRepository, MongoRepos.TimedEnchantRepository, PgRepos.TimedEnchantRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<IQuickSlotRepository, MongoRepos.QuickSlotRepository, PgRepos.QuickSlotRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<ISkillRepository, MongoRepos.SkillRepository, PgRepos.SkillRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<IQuestRepository, MongoRepos.QuestRepository, PgRepos.QuestRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<IZoneAuthTokenRepository, MongoRepos.ZoneAuthTokenRepository, PgRepos.ZoneAuthTokenRepository>(provider, ServiceLifetime.Scoped);
        services.AddProviderRepository<IAccountAccessRepository, MongoRepos.AccountAccessRepository, PgRepos.AccountAccessRepository>(provider, ServiceLifetime.Scoped);
        
        services.AddProviderRepository<IChannelPresenceStore, MongoPersist.ChannelPresenceStore, PgRepos.ChannelPresenceStore>(provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IPlayTimeRewardRepository, MongoRepos.PlayTimeRewardRepository, PgRepos.PlayTimeRewardRepository>(provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IOnlineAccountRegistry, MongoRepos.OnlineAccountRegistry, PgRepos.OnlineAccountRegistry>(provider, ServiceLifetime.Singleton);
        
        
        services.AddProviderRepository<IOneShotTokenStore, MongoPersist.OneShotTokenStore, PgRepos.OneShotTokenStore>(provider, ServiceLifetime.Singleton);

        return services;
    }

 /// <summary>
 /// Readies the configured store off the boot path: under Postgres it creates the schema
 /// (<see cref="RO2.Persistence.Postgres.PostgresSchema"/>, idempotent); under Mongo it resolves the
 /// context so a bad connection surfaces at startup. Call once per host after the container is built.
 /// </summary>
    public static async Task EnsurePersistenceReadyAsync(this IServiceProvider services, DatabaseConfig config, ILogger logger)
    {
        if (config.Provider == "Postgres")
        {
            var db = services.GetRequiredService<RO2.Persistence.Postgres.PostgresDbContext>();
            await RO2.Persistence.Postgres.PostgresSchema.EnsureAsync(db);
            logger.LogInformation("Postgres schema ready");
        }
        else
        {
            _ = services.GetRequiredService<MongoPersist.MongoDbContext>();
            logger.LogInformation("Mongo database ready");
        }
    }

 /// <summary>
 /// Register <typeparamref name="TService"/> with the Postgres implementation when
 /// <paramref name="provider"/> is "Postgres", otherwise the Mongo one, at <paramref name="lifetime"/>.
 /// The database context the chosen implementation depends on is registered by
 /// <see cref="AddPersistence"/> for that same provider.
 /// </summary>
    public static IServiceCollection AddProviderRepository<TService, TMongo, TPostgres>(
        this IServiceCollection services, string provider, ServiceLifetime lifetime)
        where TService : class
        where TMongo : class, TService
        where TPostgres : class, TService
    {
        var impl = provider == "Postgres" ? typeof(TPostgres) : typeof(TMongo);
        services.Add(new ServiceDescriptor(typeof(TService), impl, lifetime));
        return services;
    }
}
