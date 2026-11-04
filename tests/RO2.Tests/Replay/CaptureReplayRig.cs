using System.Collections.Concurrent;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The real Zone container (every service and handler the host wires, against a throwaway Mongo) behind one loopback
/// <see cref="ProudSession"/>. Captured C2S frames go through <see cref="RmiDispatcher.DispatchAsync"/>, the same entry point the
/// network loop uses, and the S2C frames our server wrote are read back off the client socket.
/// </summary>
internal sealed class CaptureReplayRig : IAsyncDisposable
{
    /// <summary>Quiet time on the client socket that ends a drain. Handlers are awaited, so this only catches fire-and-forget sends.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(20);
    private const uint AccountId = 7_700_001;

    private readonly ServiceProvider _services;
    private readonly ReauditShared.Harness _harness;
    private readonly RmiDispatcher _dispatcher;
    private readonly IMongoClient _mongo;
    private readonly string _database;
    private readonly WarningLog _log;
    private Task<(ushort Op, byte[] Body)>? _pending;

    public Character StandIn { get; }

    private CaptureReplayRig(ServiceProvider services, ReauditShared.Harness harness, RmiDispatcher dispatcher,
        IMongoClient mongo, string database, WarningLog log, Character standIn)
    {
        _services = services; _harness = harness; _dispatcher = dispatcher; _mongo = mongo; _database = database;
        _log = log; StandIn = standIn;
    }

    public static async Task<CaptureReplayRig> StartAsync(string mongoConnectionString, Guid zoneAuthToken)
    {
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

        var harness = await ReauditShared.Harness.StartAsync(1);
        provider.InitializeBroadcastService(harness.Server);
        var dispatcher = new RmiDispatcher(NullLogger.Instance);
        provider.WireZoneHandlers(dispatcher, harness.Server, NullLogger.Instance);

        // Stand-in account and character; the real LoginPC handler builds the PlayerState from this row.
        await provider.GetRequiredService<IZoneAuthTokenRepository>().IssueAsync(zoneAuthToken, AccountId, TimeSpan.FromMinutes(30));
        var job = store.StandardStats.Keys.Order().First();
        var (character, error) = await provider.GetRequiredService<ICharacterRepository>()
            .CreateCharacterAsync(AccountId, "Replay" + Guid.NewGuid().ToString("N")[..6], job, 0, "{}");
        if (character is null) throw new InvalidOperationException("could not create the stand-in character: " + error);

        return new CaptureReplayRig(provider, harness, dispatcher, new MongoClient(mongoConnectionString), database, log, character);
    }

    public bool HasHandler(ushort op) => _dispatcher.HasHandler(op);
    public string? HandlerName(ushort op) => _dispatcher.GetOpcodeName(op);

    /// <summary>
    /// Feeds one captured C2S body to its handler and returns every S2C frame our server wrote in response, plus the server's warnings.
    /// Retail's uid and actor handle are swapped for the stand-in's first (see <see cref="Rewrite"/>). <c>Closed</c> is set when the server dropped the connection.
    /// </summary>
    public async Task<(List<(ushort Op, byte[] Body)> Frames, List<string> Warnings, bool Closed)> SendAsync(ushort op, byte[] body)
    {
        _log.Drain();
        var session = _harness.Sessions[0];
        await _dispatcher.DispatchAsync(session, op, Rewrite(body)).WaitAsync(DispatchTimeout);
        if (op == 1303) StopHoldLoop(session);

        var frames = new List<(ushort, byte[])>();
        var reader = _harness.Readers[0];
        var closed = false;
        while (true)
        {
            _pending ??= reader.NextAsync();
            try { frames.Add(await _pending.WaitAsync(Idle)); }
            catch (TimeoutException) { if (_pending.IsFaulted) _pending = null; break; } // else the read stays pending for the next drain
            catch (Exception ex) when (ex is EndOfStreamException or System.Net.Sockets.SocketException or IOException)
            {
                closed = true; _pending = null; break;
            }
            _pending = null;
        }
        return (frames, _log.Drain(), closed);
    }

    /// <summary>
    /// 1303 starts an online-account hold loop that counts in <c>ZoneAuthHandler.ActiveHoldLoops</c>, a process-wide number other tests
    /// assert on. The replay does not need the heartbeat, so the loop is cancelled right away to stay out of their way.
    /// shortcut: reaches the private HoldLoops table by reflection, replace with an internal accessor if production grows one.
    /// </summary>
    private static void StopHoldLoop(ProudSession session)
    {
        var table = typeof(RO2.Server.Zone.Handlers.Rmi.ZoneAuthHandler)
            .GetField("HoldLoops", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            as System.Runtime.CompilerServices.ConditionalWeakTable<ProudSession, CancellationTokenSource>;
        if (table is not null && table.TryGetValue(session, out var cts)) cts.Cancel();
    }

    private ulong _retailUid;
    private uint _retailHandle;

    /// <summary>The identity the retail session had: the character uid and the actor handle its own 2310 carried.</summary>
    public void SetRetailIdentity(ulong uid, uint handle) { _retailUid = uid; _retailHandle = handle; }

    public uint OurHandle => PlayerStateManager.BuildActorHandle(_harness.Sessions[0].SessionId);

    /// <summary>The uid the stand-in answers to: retail's high word with the stand-in's character id in the low word (LoginPC keeps the low 32 bits).</summary>
    public ulong StandInUid => (_retailUid & 0xFFFFFFFF00000000UL) | StandIn.Id;

    /// <summary>
    /// A C2S names the character by uid and itself by actor handle; retail's values are not ours. Every exact occurrence of the retail
    /// uid (8 bytes) or handle (4 bytes) is swapped for the stand-in's. Nothing else in a body is touched.
    /// </summary>
    private byte[] Rewrite(byte[] body)
    {
        if (_retailUid == 0) return body;
        var copy = (byte[])body.Clone();
        var ours = PlayerStateManager.BuildActorHandle(_harness.Sessions[0].SessionId);
        for (var i = 0; i + 4 <= copy.Length; i++)
        {
            if (i + 8 <= copy.Length && BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(i)) == _retailUid)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(i), StandInUid);
                i += 7;
            }
            else if (BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(i)) == _retailHandle)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(i), ours);
                i += 3;
            }
        }
        return copy;
    }

    public async ValueTask DisposeAsync()
    {
        // Unobserved: the pending read faults when the socket closes.
        _pending?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        await _harness.DisposeAsync();
        await _services.DisposeAsync();
        try { await _mongo.DropDatabaseAsync(_database); } catch (Exception) { /* throwaway container */ }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>Collects Warning and above, so a window that came out wrong can say why.</summary>
    private sealed class WarningLog : ILoggerProvider
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
