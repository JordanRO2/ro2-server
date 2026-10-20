using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.BuildingBlocks.Bus;
using RO2.Identity.Application.Commands;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure;
using RO2.Identity.Infrastructure.Mongo;
using RO2.Infrastructure;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Handlers;
using RO2.Network.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Login.Configuration;
using RO2.Server.Login.Services;
using Serilog;

namespace RO2.Server.Login;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var config = LoadConfiguration();

        var host = Host.CreateDefaultBuilder(args)
            .UseServerLogging("Login", "logs")
            .ConfigureServices(services => ConfigureServices(services, config))
            .Build();

        
        LoggingConfiguration.WriteStartupBanner("Login");

        
        await InitializeDatabaseAsync(host.Services, config);

        
        await StartServerAsync(host, config);

        await host.RunAsync();
    }

    public static void ConfigureServices(IServiceCollection services, ServerConfiguration config)
    {
        services.AddSingleton(config);
        services.AddPersistence(config.Database);

        
        
        
        
        
        
        
        if (config.Database.Provider == "Postgres")
        {
            services.AddSingleton(new RO2.Identity.Infrastructure.Postgres.IdentityPostgresContext(
                config.Database.ConnectionString));
            services.AddScoped<RO2.Identity.Domain.Account.IAccountRepository,
                RO2.Identity.Infrastructure.Postgres.PostgresAccountRepository>();
        }
        else
        {
            services.AddSingleton(new IdentityMongoContext(
                config.Database.ConnectionString, config.Database.DatabaseName));
            services.AddScoped<RO2.Identity.Domain.Account.IAccountRepository, MongoAccountRepository>();
        }
        services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<IClock, SystemClock>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddScoped<AuthenticateCommandHandler>();
        services.AddScoped<RegisterAccountCommandHandler>();
        services.AddScoped<SetSecondPasswordCommandHandler>();
        services.AddScoped<VerifySecondPasswordCommandHandler>();

        services.AddScoped<LoginService>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<SecondPasswordThrottle>();

        
        services.AddLoginHandlers(Assembly.GetExecutingAssembly());
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services, ServerConfiguration config)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
        
        
        await services.EnsurePersistenceReadyAsync(config.Database, logger);
    }

    private static async Task StartServerAsync(IHost host, ServerConfiguration config)
    {
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var serverLogger = loggerFactory.CreateLogger("LoginServer");
        var server = new ProudServer("Login", serverLogger);
        var dispatcher = new RmiDispatcher(serverLogger);

        
        host.Services.WireLoginHandlers(dispatcher, server, serverLogger);

        
        server.PacketReceived += async (session, opcode, payload) =>
        {
            serverLogger.LogDebug("Session {SessionId}: RMI packet received - Opcode: 0x{Opcode:X4} ({Size} bytes)",
                session.SessionId, opcode, payload.Length);
            await dispatcher.DispatchAsync(session, opcode, payload);
        };

        
        server.SessionConnected += async session =>
        {
            serverLogger.LogInformation("Client connected: Session {SessionId} from {Endpoint}",
                session.SessionId, session.RemoteEndPoint);
        };

        server.SessionDisconnected += async session =>
        {
            serverLogger.LogInformation("Client disconnected: Session {SessionId}", session.SessionId);
        };

        var endpoint = new IPEndPoint(IPAddress.Parse(config.Login.Host), config.Login.Port);
        await server.StartAsync(endpoint);

        serverLogger.LogInformation("Login Server listening on {Endpoint}", endpoint);
        serverLogger.LogInformation("Press Ctrl+C to stop...");
    }

    
    
    
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
