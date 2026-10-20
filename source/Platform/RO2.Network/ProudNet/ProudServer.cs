using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RO2.Network.ProudNet;

/// <summary>
/// Transport limits for <see cref="ProudServer"/>. They bound what an unauthenticated peer can hold
/// open; every value is a ceiling far above legitimate traffic. Pass a customised instance to the
/// <see cref="ProudServer"/> constructor to tune them.
/// </summary>
public sealed class ProudServerOptions
{
    /// <summary>
    /// A session that has sent nothing for this long is closed. The client's keepalive (RMI 1001 and
    /// the core pings) fires every few seconds, so 120 s means several missed beats.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// A session that has not completed the ProudNet handshake this long after accept is closed. The
    /// handshake is a handful of round trips and finishes in well under a second on a healthy link.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the sweep looks for timed-out sessions (one timer per server, not per session).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Concurrent sessions accepted by one server; further connections are closed on accept.</summary>
    public int MaxSessions { get; init; } = 4096;

    /// <summary>
    /// Concurrent sessions per remote IP address. Generous because several clients can legitimately
    /// share one NAT address (multi-boxing, households, internet cafes), but far below what one host
    /// needs to exhaust the server.
    /// </summary>
    public int MaxSessionsPerIp { get; init; } = 32;

    /// <summary>Clock for the timeouts; tests inject a manual one.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// ProudNet-compatible TCP server
/// </summary>
public class ProudServer : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, ProudSession> _sessions = new();
    private readonly ProudServerOptions _options;
    private readonly Dictionary<IPAddress, int> _perIpCount = new();
    private Task? _sweepTask;

    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private uint _nextSessionId;
    private bool _disposed;

    public string Name { get; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public int SessionCount => _sessions.Count;
    public bool IsRunning => _listener?.IsBound ?? false;

    public event Func<ProudSession, Task>? SessionConnected;
    public event Func<ProudSession, Task>? SessionDisconnected;
    public event Func<ProudSession, ushort, ReadOnlyMemory<byte>, Task>? PacketReceived;
    public event Func<ProudSession, ProudCoreOpCode, ReadOnlyMemory<byte>, Task>? CoreMessageReceived;

    public ProudServer(string name, ILogger logger, ProudServerOptions? options = null)
    {
        Name = name;
        _logger = logger;
        _options = options ?? new ProudServerOptions();
    }

    /// <summary>
    /// Start listening for connections
    /// </summary>
    public async Task StartAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        if (_listener != null)
            throw new InvalidOperationException("Server already started");

        _listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.NoDelay = true;

        _listener.Bind(endpoint);
        _listener.Listen(128);

        LocalEndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        _logger.LogInformation("{Name} server started on {Endpoint}", Name, LocalEndPoint);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = AcceptLoopAsync(_cts.Token);
        _sweepTask = SweepLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stop the server
    /// </summary>
    public async Task StopAsync()
    {
        if (_listener == null)
            return;

        _logger.LogInformation("{Name} server stopping...", Name);

        _cts?.Cancel();

        // Close listener
        try
        {
            _listener.Close();
        }
        catch { }

        // Wait for accept loop
        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        if (_sweepTask != null)
        {
            try
            {
                await _sweepTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        // Disconnect all sessions
        var disconnectTasks = _sessions.Values.Select(s => s.DisconnectAsync());
        await Task.WhenAll(disconnectTasks);

        // Dispose all sessions
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        _logger.LogInformation("{Name} server stopped", Name);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("{Name}: Accept loop started, waiting for connections...", Name);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listener!.AcceptAsync(ct);

                // Refuse beyond the caps before any per-session state exists. Listen(128) is only
                // the kernel backlog, not a limit on live sessions.
                if (!TryReserveSlot(clientSocket))
                {
                    clientSocket.Dispose();
                    continue;
                }

                clientSocket.NoDelay = true;

                var sessionId = Interlocked.Increment(ref _nextSessionId);
                var session = new ProudSession(clientSocket, sessionId, _logger, _options.TimeProvider);

                // Log immediately at accept level
                _logger.LogInformation("{Name}: TCP connection ACCEPTED from {RemoteEndPoint}, creating session {SessionId}",
                    Name, clientSocket.RemoteEndPoint, sessionId);

                if (_sessions.TryAdd(sessionId, session))
                {
                    session.PacketReceived += OnPacketReceived;
                    session.CoreMessageReceived += OnCoreMessageReceived;
                    session.Disconnected += OnSessionDisconnected;

                    _logger.LogDebug("{Name}: Session {SessionId} connected from {RemoteEndPoint}",
                        Name, sessionId, session.RemoteEndPoint);

                    // Start receiving
                    session.StartReceiving();

                    // Notify connection. Not awaited: a subscriber that takes seconds (a DB round trip, a
                    // stuck dependency) must not hold the accept loop, or every connection queued behind it
                    // waits too (S-H13). Its failure is still logged.
                    NotifySessionConnected(session);
                }
                else
                {
                    ReleaseSlot(session.RemoteEndPoint?.Address);
                    await session.DisposeAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Name}: Error accepting connection", Name);
                await Task.Delay(100, ct);
            }
        }
    }

    private void NotifySessionConnected(ProudSession session)
    {
        var handler = SessionConnected;
        if (handler == null)
            return;

        // The handler's synchronous part runs here, in order, as it always did; only its remaining
        // asynchronous work is left to finish on its own.
        Task pending;
        try
        {
            pending = handler.Invoke(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name}: Error in session connected handler", Name);
            return;
        }

        if (pending.IsCompletedSuccessfully)
            return;

        _ = ObserveAsync(pending);

        async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Name}: Error in session connected handler", Name);
            }
        }
    }

    private static IPAddress NormalizeIp(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>
    /// Count the new connection against the global and per-IP caps. Returns false (and logs at debug)
    /// when either is full; the caller closes the socket. Runs only on the single accept loop, so the
    /// global check cannot race with another accept; the per-IP map is locked because sessions release
    /// from their own threads.
    /// </summary>
    private bool TryReserveSlot(Socket socket)
    {
        IPAddress ip;
        try
        {
            if (socket.RemoteEndPoint is not IPEndPoint remote)
                return false;
            ip = NormalizeIp(remote.Address);
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }

        if (_sessions.Count >= _options.MaxSessions)
        {
            _logger.LogDebug("{Name}: refusing {Ip}: {Max} concurrent sessions reached", Name, ip, _options.MaxSessions);
            return false;
        }

        lock (_perIpCount)
        {
            _perIpCount.TryGetValue(ip, out var current);
            if (current >= _options.MaxSessionsPerIp)
            {
                _logger.LogDebug("{Name}: refusing {Ip}: {Max} sessions from this address reached", Name, ip, _options.MaxSessionsPerIp);
                return false;
            }
            _perIpCount[ip] = current + 1;
        }
        return true;
    }

    private void ReleaseSlot(IPAddress? ip)
    {
        if (ip == null)
            return;
        ip = NormalizeIp(ip);
        lock (_perIpCount)
        {
            if (!_perIpCount.TryGetValue(ip, out var current))
                return;
            if (current <= 1)
                _perIpCount.Remove(ip);
            else
                _perIpCount[ip] = current - 1;
        }
    }

    /// <summary>
    /// One timer for the whole server (not a timer or thread per session): wakes every
    /// <see cref="ProudServerOptions.SweepInterval"/> and closes sessions that overstayed.
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.SweepInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await SweepIdleSessionsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name}: idle sweep stopped", Name);
        }
    }

    /// <summary>
    /// Close every session past its handshake or idle window as of the injected clock. Public so a
    /// test (or an operator hook) can run a pass without waiting for the timer.
    /// </summary>
    /// <returns>How many sessions were closed.</returns>
    public async Task<int> SweepIdleSessionsAsync()
    {
        var now = _options.TimeProvider.GetUtcNow().UtcDateTime;
        var closing = new List<Task>();
        foreach (var session in _sessions.Values)
        {
            var reason = session.GetTimeoutReason(now, _options.HandshakeTimeout, _options.IdleTimeout);
            if (reason == null)
                continue;

            _logger.LogInformation("{Name}: closing session {SessionId} from {RemoteEndPoint}: {Reason}",
                Name, session.SessionId, session.RemoteEndPoint, reason);
            closing.Add(session.DisconnectAsync());
        }

        if (closing.Count > 0)
            await Task.WhenAll(closing);
        return closing.Count;
    }

    private async Task OnPacketReceived(ProudSession session, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        if (PacketReceived != null)
        {
            await PacketReceived.Invoke(session, opcode, payload);
        }
    }

    private async Task OnCoreMessageReceived(ProudSession session, ProudCoreOpCode opcode, ReadOnlyMemory<byte> payload)
    {
        if (CoreMessageReceived != null)
        {
            await CoreMessageReceived.Invoke(session, opcode, payload);
        }
    }

    private async Task OnSessionDisconnected(ProudSession session)
    {
        if (_sessions.TryRemove(session.SessionId, out _))
        {
            ReleaseSlot(session.RemoteEndPoint?.Address);
            _logger.LogDebug("{Name}: Session {SessionId} disconnected", Name, session.SessionId);

            if (SessionDisconnected != null)
            {
                try
                {
                    await SessionDisconnected.Invoke(session);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Name}: Error in session disconnected handler", Name);
                }
            }

            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Get session by ID
    /// </summary>
    public ProudSession? GetSession(uint sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Get all sessions
    /// </summary>
    public IEnumerable<ProudSession> GetSessions() => _sessions.Values;

    /// <summary>
    /// Broadcast RMI packet to all sessions
    /// </summary>
    public async Task BroadcastRmiAsync(ushort rmiId, ReadOnlyMemory<byte> payload, Func<ProudSession, bool>? filter = null)
    {
        var sessions = filter == null
            ? _sessions.Values
            : _sessions.Values.Where(filter);

        var tasks = sessions.Select(s => s.SendRmiAsync(rmiId, payload));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Disconnect session by ID
    /// </summary>
    public async Task DisconnectSessionAsync(uint sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            await session.DisconnectAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync();

        _cts?.Dispose();
        _listener?.Dispose();
    }
}
