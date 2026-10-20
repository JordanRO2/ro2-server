using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Infrastructure;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Handlers;
using RO2.Network.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Configuration;

namespace RO2.Server.Chat;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var config = LoadConfiguration();

        var host = Host.CreateDefaultBuilder(args)
            .UseServerLogging("Chat", "logs")
            .ConfigureServices(services => ConfigureServices(services, config))
            .Build();

        LoggingConfiguration.WriteStartupBanner("Chat");

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
        await host.Services.EnsurePersistenceReadyAsync(config.Database, logger);

        await StartServerAsync(host, config);

        // Create the one-shot token TTL index off the boot path (it waits on Mongo; the ctor must not).
        // Chat consumes messenger keys on 23001; the expiry filter keeps the store correct even if this
        // never runs, so a failure here only leaves the garbage collector unbuilt.
        _ = Task.Run(async () =>
        {
            try { await host.Services.GetRequiredService<IOneShotTokenStore>().EnsureIndexesAsync(); }
            catch { /* best-effort; the consume filter does not need the index to be correct */ }
        });

        await host.RunAsync();
    }

    public static void ConfigureServices(IServiceCollection services, ServerConfiguration config)
    {
        services.AddSingleton(config);
        services.AddPersistence(config.Database);
        services.AddScoped<FriendStore>();
        services.AddScoped<BlockListRepository>();
        services.AddSingleton<FriendPresence>();
        services.AddProviderRepository<IChannelColorRepository,
            RO2.Persistence.Mongo.Repositories.ChannelColorRepository,
            RO2.Persistence.Postgres.Repositories.ChannelColorRepository>(config.Database.Provider, ServiceLifetime.Singleton);
        services.AddSingleton<ChatBroadcastService>();
        services.AddSingleton<Services.ChatSocialThrottle>();
        services.AddChatHandlers(Assembly.GetExecutingAssembly());
    }

    private static async Task StartServerAsync(IHost host, ServerConfiguration config)
    {
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var serverLogger = loggerFactory.CreateLogger("ChatServer");
        var server = new ProudServer("Chat", serverLogger);
        host.Services.GetRequiredService<ChatBroadcastService>().SetServer(server);
        var dispatcher = new RmiDispatcher(serverLogger);

        host.Services.WireChatHandlers(dispatcher, server, serverLogger);

        server.PacketReceived += async (session, opcode, payload) =>
        {
            serverLogger.LogDebug("Session {SessionId}: RMI packet - Opcode: 0x{Opcode:X4} ({Size} bytes)",
                session.SessionId, opcode, payload.Length);
            await dispatcher.DispatchAsync(session, opcode, payload);
        };

        server.SessionConnected += async session =>
        {
            serverLogger.LogInformation("Client connected: Session {SessionId} from {Endpoint}",
                session.SessionId, session.RemoteEndPoint);
        };

        var friendPresence = host.Services.GetRequiredService<FriendPresence>();
        var broadcast = host.Services.GetRequiredService<ChatBroadcastService>();
        server.SessionDisconnected += async session =>
        {
            serverLogger.LogInformation("Client disconnected: Session {SessionId}", session.SessionId);

            // ProudServer raises this after removing the session from its table and before disposing it,
            // so CharacterId is still readable and the broadcast set already excludes the closing session.
            // A messenger disconnect is announced at the moment it happens, not later.

            // 23504 NfyLogoutPlayer_PrivateChatRoom to the remaining members of each private room, then
            // 23533 NfyPrivateChatRoom_ChangeRoomMaster if the leaver was master. A character change closes the
            // messenger connection too, so it is announced the same way. Without this call the room would prune the
            // member only at the next Join/Leave/MemberList by anyone.
            try
            {
                await PrivateChatRooms.OnSessionClosedAsync(session);
            }
            catch (Exception ex)
            {
                serverLogger.LogWarning(ex, "Session {SessionId}: private-room departure (23504/23533) failed", session.SessionId);
            }

            // 23503 NfyLogoutPlayer_RegisterFriend to the online players who have this character as a
            // friend (see FriendPresence).
            try
            {
                await friendPresence.NotifyLogoutAsync(broadcast, session);
            }
            catch (Exception ex)
            {
                serverLogger.LogWarning(ex, "Session {SessionId}: friend logout notice (23503) failed", session.SessionId);
            }
        };

        var endpoint = new IPEndPoint(IPAddress.Parse(config.Chat.Host), config.Chat.Port);
        await server.StartAsync(endpoint);

        serverLogger.LogInformation("Chat Server listening on {Endpoint}", endpoint);
        serverLogger.LogInformation("Press Ctrl+C to stop...");
    }

    private static ServerConfiguration LoadConfiguration()
    {
        const string configPath = "config.json";
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ServerConfiguration>(json) ?? new ServerConfiguration();
        }
        return new ServerConfiguration();
    }
}
