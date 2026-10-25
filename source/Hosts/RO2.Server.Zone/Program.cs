using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Economy.Domain;
using RO2.Domain.GameData;
using RO2.Infrastructure;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Extensions;
using RO2.Server.Zone.Services;
using Serilog;

namespace RO2.Server.Zone;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // First, before anything saves or loads a stored type: a dye colour has alpha 0xFF, so it is above
        // int.MaxValue, and the default uint serializer writes a checked Int32. This maps every dye-colour member
        // (item rows, auction articles, mails, the guild vault) to Int64; see DyeColorBson.
        DyeColorBson.Register();

        var config = LoadConfiguration();

        var host = Host.CreateDefaultBuilder(args)
            .UseServerLogging("Zone", "logs")
            .ConfigureServices(services => ConfigureServices(services, config))
            .Build();

        // Write startup banner
        LoggingConfiguration.WriteStartupBanner("Zone");

        // Initialize database
        await InitializeDatabaseAsync(host.Services, config);

        // Load game data
        await LoadGameDataAsync(host.Services);

        // Start zone server
        await StartServerAsync(host, config);

        // Create the one-shot token TTL index off the boot path (it waits on Mongo; the ctor must not).
        // Zone issues messenger keys on 1516; the expiry filter keeps the store correct even if this never
        // runs, so a failure here only leaves the garbage collector unbuilt.
        _ = Task.Run(async () =>
        {
            try { await host.Services.GetRequiredService<IOneShotTokenStore>().EnsureIndexesAsync(); }
            catch { /* best-effort; the consume filter does not need the index to be correct */ }
        });

        // Create the unique inventory placement index on (CharacterId, StorageType, Slot) off the boot path
        // (it waits on Mongo; the repository ctor must not). The swap handlers write collision-free, so the
        // index only backstops cross-process races. EnsureIndexesAsync returns false (never throws) when
        // existing rows already collide — leave the index unbuilt and warn so the operator runs RepairSlotsAsync.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = host.Services.CreateScope();
                if (!await scope.ServiceProvider.GetRequiredService<IInventoryRepository>().EnsureIndexesAsync())
                    Serilog.Log.Warning("Inventory slot index not created: existing rows collide on " +
                        "(CharacterId, StorageType, Slot>=0). Run RepairSlotsAsync per affected character, then restart.");
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Inventory slot index creation failed (best-effort)."); }
        });

        // Create the unique (CharacterId, SkillId) skill index off the boot path (it waits on Mongo; the ctor must
        // not). The skill upserts write collision-free, so the index only backstops cross-process races.
        // EnsureIndexesAsync returns false (never throws) when existing rows already collide — leave it unbuilt and warn.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = host.Services.CreateScope();
                if (!await scope.ServiceProvider.GetRequiredService<ISkillRepository>().EnsureIndexesAsync())
                    Serilog.Log.Warning("Skill index not created: existing rows collide on (CharacterId, SkillId). " +
                        "Clean up the duplicate skill rows, then restart.");
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Skill index creation failed (best-effort)."); }
        });

        await host.RunAsync();
    }

    public static void ConfigureServices(IServiceCollection services, ServerConfiguration config)
    {
        services.AddSingleton(config);
        services.AddPersistence(config.Database);

        // Guild persistence. Handlers and services resolve these from their per-message scope instead of building
        // them from MongoDbContext. All stateless over the singleton context, so singletons. GuildService.Instance
        // and the two stores below keep their static Instance (packet builders read them without a scope); the
        // factories only bind the context the stores load through, and each caller still runs InitAsync lazily.
        services.AddProviderRepository<IGuildStore, GuildRepository, PostgresGuildRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IGuildStorageLog, RO2.Persistence.Mongo.Repositories.GuildStorageLogRepository, RO2.Persistence.Postgres.Repositories.GuildStorageLogRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IGuildEmblemRepository, RO2.Persistence.Mongo.Repositories.GuildEmblemRepository, RO2.Persistence.Postgres.Repositories.GuildEmblemRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IGuildDepartureRepository, RO2.Persistence.Mongo.Repositories.GuildDepartureRepository, RO2.Persistence.Postgres.Repositories.GuildDepartureRepository>(config.Database.Provider, ServiceLifetime.Singleton);

        // Instance stores over the singleton MongoDbContext (DB Phase 0, pass 2b). Stateless apart from their
        // collection handles, so singletons; handlers resolve them from the per-message scope, never into a field.
        services.AddProviderRepository<ICashMallRepository, RO2.Economy.Infrastructure.Mongo.CashMallRepository, RO2.Economy.Infrastructure.Postgres.CashMallRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IVehicleRepository, RO2.Persistence.Mongo.Repositories.VehicleRepository, RO2.Persistence.Postgres.Repositories.VehicleRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IKeyConfigRepository, RO2.Persistence.Mongo.Repositories.KeyConfigRepository, RO2.Persistence.Postgres.Repositories.KeyConfigRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IAccountGiftRepository, RO2.Persistence.Mongo.Repositories.AccountGiftRepository, RO2.Persistence.Postgres.Repositories.AccountGiftRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IPresentPasswordRepository, RO2.Persistence.Mongo.Repositories.PresentPasswordRepository, RO2.Persistence.Postgres.Repositories.PresentPasswordRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IMailRepository, RO2.Persistence.Mongo.Repositories.MailRepository, RO2.Persistence.Postgres.Repositories.MailRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IDungeonEntryCountRepository, RO2.Persistence.Mongo.Repositories.DungeonEntryCountRepository, RO2.Persistence.Postgres.Repositories.DungeonEntryCountRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<ICraftRecipeRepository, RO2.Economy.Infrastructure.Mongo.CraftRecipeRepository, RO2.Economy.Infrastructure.Postgres.CraftRecipeRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<ICardBookChargeRepository, RO2.Persistence.Mongo.Repositories.CardBookChargeRepository, RO2.Persistence.Postgres.Repositories.CardBookChargeRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IPrivateShopCraftRepository, RO2.Economy.Infrastructure.Mongo.PrivateShopCraftRepository, RO2.Economy.Infrastructure.Postgres.PrivateShopCraftRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IPetIncubatorRepository, RO2.Persistence.Mongo.Repositories.PetIncubatorRepository, RO2.Persistence.Postgres.Repositories.PetIncubatorRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IUnionMembershipRepository, RO2.Persistence.Mongo.Repositories.UnionMembershipRepository, RO2.Persistence.Postgres.Repositories.UnionMembershipRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddSingleton<PlayTimeRewardStore>();
        services.AddProviderRepository<ICardRegistryRepository, RO2.Persistence.Mongo.Repositories.CardRegistryRepository, RO2.Persistence.Postgres.Repositories.CardRegistryRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IGameMallListRepository, RO2.Economy.Infrastructure.Mongo.GameMallListRepository, RO2.Economy.Infrastructure.Postgres.GameMallListRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<ICardBookRepository, RO2.Persistence.Mongo.Repositories.CardBookRepository, RO2.Persistence.Postgres.Repositories.CardBookRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<ICollectionRepository, RO2.Persistence.Mongo.Repositories.CollectionRepository, RO2.Persistence.Postgres.Repositories.CollectionRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IMonsterCollectionProgressRepository, RO2.Persistence.Mongo.Repositories.MonsterCollectionProgressRepository, RO2.Persistence.Postgres.Repositories.MonsterCollectionProgressRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IKharaTitleRepository, RO2.Persistence.Mongo.Repositories.KharaTitleRepository, RO2.Persistence.Postgres.Repositories.KharaTitleRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddSingleton<KharaTitleStore>();
        services.AddProviderRepository<IKharaStateRepository, RO2.Persistence.Mongo.Repositories.KharaStateRepository, RO2.Persistence.Postgres.Repositories.KharaStateRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IKharaTraceRepository, RO2.Persistence.Mongo.Repositories.KharaTraceRepository, RO2.Persistence.Postgres.Repositories.KharaTraceRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IWorldEventStateRepository, RO2.Persistence.Mongo.Repositories.WorldEventStateRepository, RO2.Persistence.Postgres.Repositories.WorldEventStateRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IIllusionPyramidStateRepository, RO2.Persistence.Mongo.Repositories.IllusionPyramidStateRepository, RO2.Persistence.Postgres.Repositories.IllusionPyramidStateRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddProviderRepository<IRankingRepository, RO2.Persistence.Mongo.Repositories.RankingRepository, RO2.Persistence.Postgres.Repositories.RankingRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        // Auction store: Zone-only. AuctionService is built per call (AuctionService.From, and a fresh scope per expiry-sweep
        // pass), and the repository ctor retries its index build until one succeeds, so keep it transient like the
        // per-call `new AuctionRepository(ctx)` it replaces.
        services.AddProviderRepository<IAuctionRepository, RO2.Persistence.Mongo.Repositories.AuctionRepository, RO2.Persistence.Postgres.Repositories.AuctionRepository>(config.Database.Provider, ServiceLifetime.Transient);

        // Game data (loaded at startup, immutable)
        services.AddSingleton<GameDataStore>();

        // Zone-specific services (singletons for cross-handler state)
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<MovementGuard>();
        services.AddSingleton<PlayerStateManager>();
        services.AddSingleton<NpcStateManager>();
        services.AddSingleton<BroadcastService>();
        services.AddSingleton<PlayerSpawnService>();
        services.AddSingleton<NpcSpawnService>();
        services.AddSingleton<PartyService>();
        services.AddSingleton<CombatService>();
        services.AddSingleton<LootService>();
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<ZoneService>();
        // Pending channel moves (RMI 1420 -> 2556 -> reconnect -> 1421). The 1421 arrives on a NEW
        // connection that has no PlayerState yet, so this singleton is the only thing carrying
        // (account, character, target channel) across the reconnect. It MUST be a singleton: a scoped
        // or transient store would resolve fine and then lose every ticket, stranding the player.
        services.AddSingleton<ChannelMoveTicketStore>();
        // Durability wear (RMI 2503). Event-driven per hit, so a plain singleton — NOT hosted. The
        // weapon path is resolved from the per-message scope by NormalAttackDamageHandler; the armour
        // path (hit taken) is injected into NpcCombatService below, so this must stay a singleton.
        services.AddSingleton<DurabilityService>();
        // Timed effects (RMI 2492/2493/2494). Registered as a singleton so ItemUseHandler can resolve
        // the SAME instance that applies effects, and ALSO as a hosted service (via the shared-instance
        // factory) so its PeriodicTimer expiry tick runs. A bare AddHostedService<EffectService>() would
        // not satisfy GetRequiredService<EffectService>() and would spawn a second, unshared instance.
        services.AddSingleton<EffectService>();
        services.AddHostedService(sp => sp.GetRequiredService<EffectService>());
        services.AddHostedService<RegenService>();
        // Real-time write-behind persistence (replaces the old fixed-interval checkpoint). ONE instance
        // fills three roles: the IDirtySink the live model marks on every change, the FlushAsync used at
        // logout/channel boundaries, and the hosted drain loop that converges the DB in real time.
        services.AddSingleton<PersistenceService>();
        services.AddSingleton<IDirtySink>(sp => sp.GetRequiredService<PersistenceService>());
        services.AddHostedService(sp => sp.GetRequiredService<PersistenceService>());
        services.AddHostedService<ServerTimeService>();
        // Daily RMI 2774 NfyDungeonBindReset. Retail sends it from a server timer with no C2S cause, so
        // without this hosted registration nothing ever sends it. Its three constructor dependencies
        // (PlayerStateManager, BroadcastService, GameDataStore) are the singletons registered above.
        services.AddHostedService<DungeonBindResetService>();
        // Close-range chat room signs (RMI 2765). The owner leaves the room on the CHAT server (23022), so the
        // Zone learns of it only through the Chat-to-Zone signal collection this service tails
        // (ICloseRangeRoomSignals). Without this registration no 2765 is ever sent and every 2763 sign stays up.
        // Its dependencies (ICloseRangeRoomSignals via AddPersistence, PlayerStateManager, BroadcastService) are singletons above.
        services.AddHostedService<CloseRangeExitListener>();
        // World events (RMI 3030, WorldEvent_Main). ONE instance, two roles, like EffectService: AnsMapLoadingHandler
        // reads the live stages for the world-entry burst and MakeOfferingsHandler (1693) counts offerings through the
        // singleton, and the hosted loop times stages out (stage 5 of 122101 falls back after 1,380 minutes) and pushes
        // each change to the players online. A bare AddHostedService would run a second, unshared instance. Its
        // dependencies (GameDataStore, PlayerStateManager, BroadcastService, MongoDbContext) are singletons above.
        services.AddSingleton<WorldEventService>();
        services.AddHostedService(sp => sp.GetRequiredService<WorldEventService>());
        // Timed enchants (1543 blessings, whetstones, oils, embroideries: record+0x11/+0x13). The hosted loop counts
        // the worn items' seconds down 10 per 10 s and re-sends the 2498 when one runs out; nothing else resolves it,
        // so a bare hosted registration is enough. Its dependencies (PlayerStateManager, BroadcastService,
        // EffectService, GameDataStore, MongoDbContext) are the singletons above.
        services.AddHostedService<TimedEnchantService>();

        // Central variable-rate game loop (TickConfig, 20-128 Hz, like modern shooters). The
        // simulation subsystems below register as ITickable and are advanced by the loop instead of
        // each owning a private PeriodicTimer, so there is ONE authority on simulated time. Note the
        // split: housekeeping (regen, effects, server time) and persistence (autosave) stay decoupled
        // as their own hosted services above — they must NOT run at tick rate.
        services.AddSingleton<NpcMovementService>();
        services.AddSingleton<NpcCombatService>();
        services.AddSingleton<ITickable>(sp => sp.GetRequiredService<NpcMovementService>());
        services.AddSingleton<ITickable>(sp => sp.GetRequiredService<NpcCombatService>());
        // NPC chat bubbles (RMI 2729). ONE instance plays two roles. NormalAttackDamageHandler
        // resolves it to speak the combat line next to the state-3 2398, and the game loop ticks
        // it for idle chatter. Both must reach the same instance, because it holds the
        // per-engagement set that stops a repeated 2398 from repeating the line.
        services.AddSingleton<NpcChatService>();
        services.AddSingleton<ITickable>(sp => sp.GetRequiredService<NpcChatService>());
        // Chase: moves an aggroed monster toward its target until it is in swing reach, and leashes it home.
        // Safe now that 2318 carries the real navmesh cell of the destination (GameDataStore.GetNavCellAt).
        services.AddSingleton<NpcChaseService>();
        services.AddSingleton<ITickable>(sp => sp.GetRequiredService<NpcChaseService>());
        // Player "in battle" combat stance (2398 OnChangeState 0x0C). Entered on the player's attack/hit and
        // dropped after an out-of-combat timeout; the handlers mark combat, the loop ticks the leave sweep.
        services.AddSingleton<PcCombatStateService>();
        services.AddSingleton<ITickable>(sp => sp.GetRequiredService<PcCombatStateService>());
        services.AddHostedService<GameLoopService>();

        // Register all handlers from this assembly
        services.AddZoneHandlers(Assembly.GetExecutingAssembly());
    }

    private static async Task LoadGameDataAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("GameData");
        var store = services.GetRequiredService<GameDataStore>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        logger.LogInformation("Loading game data from JSON...");
        await store.LoadAsync("data");
        sw.Stop();

        var totalSpawns = store.MapSpawns.Values.Sum(s => s.Length);
        logger.LogInformation("Game data loaded in {Ms}ms: {Items} items, {Maps} maps, {Quests} quests, {Skills} skills, {Npcs} NPCs, {Merchants} merchants, {Spawns} spawns across {SpawnMaps} maps",
            sw.ElapsedMilliseconds, store.Items.Count, store.Maps.Count, store.Quests.Count,
            store.Skills.Count, store.Npcs.Count, store.MerchantItems.Count, totalSpawns, store.MapSpawns.Count);
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services, ServerConfiguration config)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
        await services.EnsurePersistenceReadyAsync(config.Database, logger);
    }

    private static async Task StartServerAsync(IHost host, ServerConfiguration config)
    {
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var serverLogger = loggerFactory.CreateLogger("ZoneServer");
        var server = new ProudServer("Zone", serverLogger);
        var dispatcher = new RmiDispatcher(serverLogger);

        // Initialize broadcast service with server reference
        host.Services.InitializeBroadcastService(server);

        // Wire up all handlers using auto-discovery
        host.Services.WireZoneHandlers(dispatcher, server, serverLogger);

        // Wire up RMI packet dispatch
        server.PacketReceived += async (session, opcode, payload) =>
        {
            // Runs for every packet. Unguarded, LogDebug builds its params object[] and boxes the uint, ushort
            // and int before the logger checks the level, so it allocated on each packet even with Debug off.
            // Behind IsEnabled it allocates nothing then; the text is unchanged when Debug is on.
            if (serverLogger.IsEnabled(LogLevel.Debug))
                serverLogger.LogDebug("Session {SessionId}: RMI packet received - Opcode: 0x{Opcode:X4} ({Size} bytes)",
                    session.SessionId, opcode, payload.Length);
            await dispatcher.DispatchAsync(session, opcode, payload);
        };

        // Wire up connection events
        var zoneService = host.Services.GetRequiredService<ZoneService>();

        server.SessionConnected += async session =>
        {
            serverLogger.LogInformation("Client connected: Session {SessionId} from {Endpoint}",
                session.SessionId, session.RemoteEndPoint);
        };

        server.SessionDisconnected += async session =>
        {
            await zoneService.HandlePlayerDisconnectAsync(session.SessionId, host.Services);
            serverLogger.LogInformation("Client disconnected: Session {SessionId}", session.SessionId);
        };

        var endpoint = new IPEndPoint(IPAddress.Parse(config.Zone.Host), config.Zone.Port);
        await server.StartAsync(endpoint);

        serverLogger.LogInformation("Zone Server listening on {Endpoint}", endpoint);
        serverLogger.LogInformation("Press Ctrl+C to stop...");
    }

    // One shared instance (CA1869, an error repo-wide): JsonSerializerOptions caches the per-type
    // serialization metadata, so a fresh instance per call rebuilds it every time. This path runs once
    // at startup, so the point is keeping the rule enforceable, not the saving. Same options as before.
    private static readonly JsonSerializerOptions DefaultConfigJsonOptions = new() { WriteIndented = true };

    private static ServerConfiguration LoadConfiguration()
    {
        const string configPath = "config.json";
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ServerConfiguration>(json) ?? new ServerConfiguration();
        }

        var config = new ServerConfiguration();
        var json2 = JsonSerializer.Serialize(config, DefaultConfigJsonOptions);
        File.WriteAllText(configPath, json2);
        Log.Information("Created default configuration: {ConfigPath}", configPath);
        return config;
    }
}
