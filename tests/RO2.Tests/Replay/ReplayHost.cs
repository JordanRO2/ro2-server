using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Extensions;
using RO2.Server.Zone.Services;
using RO2.Tests.Packets;
using ZoneConfig = RO2.Server.Zone.Configuration.ServerConfiguration;
using ZoneProgram = RO2.Server.Zone.Program;

namespace RO2.Tests.Replay;

/// <summary>
/// The expensive half of a replay, booted once: the Zone container (every service and handler the host wires, against a throwaway
/// Mongo database), the loaded <see cref="GameDataStore"/>, and a loopback <see cref="ProudServer"/>. Sessions come and go on it
/// (<see cref="ConnectAsync"/> / <see cref="EndSessionAsync"/>), which is what makes replaying hundreds of captures affordable.
/// </summary>
internal sealed class ReplayHost : IAsyncDisposable
{
    /// <summary>First character id a reused host hands out. Statics keyed by character id (Khara's published state ...) may hold ids a
    /// per-file rig of this process used before, which start at 1, so a reused host stays clear of them.</summary>
    private const long FirstCharacterId = 100_000;

    private readonly IMongoClient _mongo;
    private readonly string _database;
    private readonly string _previousCwd;

    public ServiceProvider Services { get; }
    public GameDataStore Store { get; }
    public ProudServer Server { get; }
    public RmiDispatcher Dispatcher { get; }
    public WarningLog Log { get; }
    /// <summary>Quiet time on the client socket that ends a drain. Handlers are awaited, so this only catches fire-and-forget sends.</summary>
    public TimeSpan Idle { get; }

    private ReplayHost(ServiceProvider services, GameDataStore store, ProudServer server, RmiDispatcher dispatcher, WarningLog log,
        IMongoClient mongo, string database, string previousCwd, TimeSpan idle)
    {
        Services = services; Store = store; Server = server; Dispatcher = dispatcher; Log = log;
        _mongo = mongo; _database = database; _previousCwd = previousCwd; Idle = idle;
    }

    public static async Task<ReplayHost> BootAsync(string mongoConnectionString, TimeSpan? idle = null)
    {
        // Handlers load data/mall, data/npc, data/ItemInfo.json ... by a path relative to the process, which under the test host is the
        // bin folder: they came back empty (2866 had no body). The repo root is the parent of the data folder the store loads from.
        var previousCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetDirectoryName(ReauditShared.RequireDataDirectory())!);

        var database = "ro2_replay_" + Guid.NewGuid().ToString("N");
        var config = new ZoneConfig();
        config.Database.ConnectionString = mongoConnectionString;
        config.Database.DatabaseName = database;

        RO2.Server.Zone.Services.DyeColorBson.Register();
        var log = new WarningLog();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log));
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        ZoneProgram.ConfigureServices(services, config);
        var provider = services.BuildServiceProvider();

        // Hosted services (ticks, regen, persistence loops) are deliberately NOT started: the only S2C we read is what a C2S caused.
        var store = provider.GetRequiredService<GameDataStore>();
        await store.LoadAsync(ReauditShared.RequireDataDirectory());

        var server = new ProudServer("test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        provider.InitializeBroadcastService(server);
        var dispatcher = new RmiDispatcher(NullLogger.Instance);
        provider.WireZoneHandlers(dispatcher, server, NullLogger.Instance);

        var host = new ReplayHost(provider, store, server, dispatcher, log, new MongoClient(mongoConnectionString), database,
            previousCwd, idle ?? TimeSpan.FromMilliseconds(120));
        await host.PlantCharacterIdFloorAsync(provider);
        return host;
    }

    /// <summary>CreateCharacterAsync numbers a new character max(Id)+1; one soft-deleted row at <see cref="FirstCharacterId"/> sets the floor.</summary>
    private async Task PlantCharacterIdFloorAsync(IServiceProvider provider)
    {
        var repo = provider.GetRequiredService<ICharacterRepository>();
        var (scratch, error) = await repo.CreateCharacterAsync(1, "idfloor", 1, 0, "{}");
        if (scratch is null) throw new InvalidOperationException("could not create the id-floor character: " + error);
        var characters = _mongo.GetDatabase(_database).GetCollection<BsonDocument>("characters");
        var doc = await characters.Find(Builders<BsonDocument>.Filter.Empty).FirstAsync();
        await characters.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty);
        doc["_id"] = doc["_id"].IsInt32 ? (BsonValue)(int)FirstCharacterId : (BsonValue)FirstCharacterId;
        doc["IsDeleted"] = true;
        await characters.InsertOneAsync(doc);
    }

    /// <summary>A new loopback client and the server-side session it opened.</summary>
    public async Task<(Socket Client, ProudSession Session)> ConnectAsync()
    {
        var tcs = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<ProudSession, Task> onConnected = s => { tcs.TrySetResult(s); return Task.CompletedTask; };
        Server.SessionConnected += onConnected;
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await client.ConnectAsync(Server.LocalEndPoint!);
            return (client, await tcs.Task.WaitAsync(ReauditShared.Generous));
        }
        catch { client.Dispose(); throw; }
        finally { Server.SessionConnected -= onConnected; }
    }

    /// <summary>
    /// Ends a session the way the host does on a socket close (<c>ZoneService.HandlePlayerDisconnectAsync</c>: ride, trade, PlayerState, final
    /// flush) and then wipes the database, so the next session starts from nothing it can see.
    /// </summary>
    public async Task EndSessionAsync(Socket client, ProudSession session)
    {
        client.Dispose();
        try { await Services.GetRequiredService<ZoneService>().HandlePlayerDisconnectAsync(session.SessionId, Services); }
        catch (Exception) { /* the wipe below is what matters */ }
        await ResetAsync();
    }

    /// <summary>
    /// Empties every collection (indexes stay). The characters collection keeps its highest-id row, soft-deleted, so ids only ever grow
    /// and the statics keyed by character id (loaded once per process) never see an id twice.
    /// shortcut: besides the attendance day cache (keyed by ACCOUNT id, and sessions of one account repeat in the corpus) no static keyed by
    /// something other than session or character id is cleared; the mass test re-replays its first session at the end and reports whether the
    /// result drifted, which is how a missed one would show.
    /// </summary>
    private async Task ResetAsync()
    {
        (typeof(AttendanceDayGate).GetField("KnownAttendedDay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            as ConcurrentDictionary<uint, int>)?.Clear();
        var db = _mongo.GetDatabase(_database);
        foreach (var name in await (await db.ListCollectionNamesAsync()).ToListAsync())
        {
            if (name.StartsWith("system.", StringComparison.Ordinal)) continue;
            var c = db.GetCollection<BsonDocument>(name);
            if (name != "characters") { await c.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty); continue; }
            var top = await c.Find(Builders<BsonDocument>.Filter.Empty).Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(1).FirstOrDefaultAsync();
            if (top is null) continue;
            await c.DeleteManyAsync(Builders<BsonDocument>.Filter.Ne("_id", top["_id"]));
            await c.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", top["_id"]), Builders<BsonDocument>.Update.Set("IsDeleted", true));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync();
        await Services.DisposeAsync();
        try { await _mongo.DropDatabaseAsync(_database); } catch (Exception) { /* throwaway container */ }
        Directory.SetCurrentDirectory(_previousCwd);
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>Collects Warning and above, so a window that came out wrong can say why.</summary>
    internal sealed class WarningLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);
        public List<string> Drain() { var l = new List<string>(); while (_lines.TryDequeue(out var s)) l.Add(s); return l; }
        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var text = formatter(state, exception);
                lines.Enqueue($"[{logLevel}] {category[(category.LastIndexOf('.') + 1)..]}: {text}" + (exception is null ? "" : " :: " + exception.GetType().Name + ": " + exception.Message));
            }
        }
    }
}
