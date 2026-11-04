using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using RO2.Application.Interfaces;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;
using RO2.Tests.Packets;

namespace RO2.Tests.Replay;

/// <summary>
/// One replayed session on a <see cref="ReplayHost"/> (the real Zone container, every service and handler the host wires, against a
/// throwaway Mongo), behind one loopback <see cref="ProudSession"/>. Captured C2S frames go through <see cref="RmiDispatcher.DispatchAsync"/>, the same entry point the
/// network loop uses, and the S2C frames our server wrote are read back off the client socket.
/// </summary>
internal sealed class CaptureReplayRig : IAsyncDisposable
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(20);
    private const uint StandInAccountId = 7_700_001;

    private readonly ReplayHost _host;
    private readonly bool _ownsHost;
    private readonly System.Net.Sockets.Socket _client;
    private readonly ProudSession _session;
    private readonly ReauditShared.FrameReader _reader;
    private Task<(ushort Op, byte[] Body)>? _pending;

    public Character StandIn { get; }
    /// <summary>What seeding the retail character recorded (items it could not model, titles it could not wear).</summary>
    public IReadOnlyList<string> SeedNotes { get; }

    private CaptureReplayRig(ReplayHost host, bool ownsHost, System.Net.Sockets.Socket client, ProudSession session,
        Character standIn, IReadOnlyList<string> seedNotes)
    {
        _host = host; _ownsHost = ownsHost; _client = client; _session = session;
        _reader = new ReauditShared.FrameReader(client);
        StandIn = standIn; SeedNotes = seedNotes;
    }

    /// <summary>
    /// With a <paramref name="seed"/> the account holds the retail characters (the logged-in one rebuilt from retail's own 2310/2307),
    /// otherwise a single level-1 stand-in.
    /// </summary>
    public static async Task<CaptureReplayRig> StartAsync(string mongoConnectionString, Guid zoneAuthToken, RetailSeed? seed = null)
    {
        var host = await ReplayHost.BootAsync(mongoConnectionString);
        try { return await BeginAsync(host, true, zoneAuthToken, seed); }
        catch { await host.DisposeAsync(); throw; }
    }

    /// <summary>
    /// A session on an already booted <paramref name="host"/>: a fresh client socket and a character seeded from <paramref name="seed"/>.
    /// Disposing the rig ends the session and wipes the host's mutable state; it does not dispose the host.
    /// </summary>
    public static Task<CaptureReplayRig> BeginAsync(ReplayHost host, Guid zoneAuthToken, RetailSeed? seed)
        => BeginAsync(host, false, zoneAuthToken, seed);

    private static async Task<CaptureReplayRig> BeginAsync(ReplayHost host, bool ownsHost, Guid zoneAuthToken, RetailSeed? seed)
    {
        var provider = host.Services;
        var store = host.Store;
        var accountId = seed?.AccountId ?? StandInAccountId;
        var (client, session) = await host.ConnectAsync();
        try
        {
            // Stand-in account and character; the real LoginPC handler builds the PlayerState from this row.
            await provider.GetRequiredService<IZoneAuthTokenRepository>().IssueAsync(zoneAuthToken, accountId, TimeSpan.FromMinutes(30));
            Character character;
            IReadOnlyList<string> notes = [];
            if (seed is null)
            {
                var job = store.StandardStats.Keys.Order().First();
                var (created, error) = await provider.GetRequiredService<ICharacterRepository>()
                    .CreateCharacterAsync(accountId, "Replay" + Guid.NewGuid().ToString("N")[..6], job, 0, "{}");
                character = created ?? throw new InvalidOperationException("could not create the stand-in character: " + error);
            }
            else
            {
                // A reused host hands out ever-growing character ids (see ReplayHost.ResetAsync), so no ids need burning.
                var (all, ownIndex, seedNotes) = await RetailSeeder.SeedAsync(provider, store, seed, accountId, burnIds: ownsHost);
                character = all[ownIndex];
                notes = seedNotes;
            }
            return new CaptureReplayRig(host, ownsHost, client, session, character, notes);
        }
        catch { client.Dispose(); throw; }
    }

    public bool HasHandler(ushort op) => _host.Dispatcher.HasHandler(op);
    public string? HandlerName(ushort op) => _host.Dispatcher.GetOpcodeName(op);

    /// <summary>
    /// Feeds one captured C2S body to its handler and returns every S2C frame our server wrote in response, plus the server's warnings.
    /// Retail's uid and actor handle are swapped for the stand-in's first (see <see cref="Rewrite"/>). <c>Closed</c> is set when the server dropped the connection.
    /// </summary>
    public async Task<(List<(ushort Op, byte[] Body)> Frames, List<string> Warnings, bool Closed)> SendAsync(ushort op, byte[] body)
    {
        _host.Log.Drain();
        var session = _session;
        await _host.Dispatcher.DispatchAsync(session, op, Rewrite(body)).WaitAsync(DispatchTimeout);
        if (op == 1303) StopHoldLoop(session);

        var frames = new List<(ushort, byte[])>();
        var reader = _reader;
        var closed = false;
        // A handler that is not registered writes nothing, so there is nothing to wait for.
        if (!_host.Dispatcher.HasHandler(op)) return (frames, _host.Log.Drain(), false);
        while (true)
        {
            _pending ??= reader.NextAsync();
            try { frames.Add(await _pending.WaitAsync(_host.Idle)); }
            catch (TimeoutException) { if (_pending.IsFaulted) _pending = null; break; } // else the read stays pending for the next drain
            catch (Exception ex) when (ex is EndOfStreamException or System.Net.Sockets.SocketException or IOException)
            {
                closed = true; _pending = null; break;
            }
            _pending = null;
        }
        return (frames, _host.Log.Drain(), closed);
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

    public uint OurHandle => PlayerStateManager.BuildActorHandle(_session.SessionId);

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
        var ours = PlayerStateManager.BuildActorHandle(_session.SessionId);
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
        if (_ownsHost) { _client.Dispose(); await _host.DisposeAsync(); }
        else await _host.EndSessionAsync(_client, _session);
    }

}
