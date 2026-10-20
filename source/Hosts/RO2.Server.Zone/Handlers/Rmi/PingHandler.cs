using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1443 AnsPing: the client's answer to a server-initiated keepalive (ReqPing, 2593).
/// This is a terminal message; the server does not reply to it.
/// </summary>
/// <remarks>
/// The body is the 8-byte tick the server sent in its 2593, echoed unchanged. The server sends a new 2593
/// once per world entry (see <see cref="SendReqPingAsync"/>) and then every <see cref="RepingInterval"/>
/// while no other 2593 has gone out on the connection.
/// </remarks>
public class PingHandler : ZoneRmiHandlerBase<PingHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsPing;
    public override string Name => "Ping";

    public PingHandler(IServiceScopeFactory s, ILogger<PingHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Keepalive re-ping period, measured from the previous 2593 sent on the connection.</summary>
    internal static readonly TimeSpan RepingInterval = TimeSpan.FromSeconds(7001);

 /// <summary>The pending re-ping of each connection, so a newer 2593 can restart it.</summary>
    private static readonly ConditionalWeakTable<ProudSession, RepingTimer> Repings = new();

 /// <summary>
 /// Sends a ReqPing (2593) and (re)starts the connection's <see cref="RepingInterval"/> timer.
 /// The 8-byte body is the server's u64 millisecond tick, which the client echoes back in 1443.
 /// </summary>
    internal static async Task SendReqPingAsync(ProudSession session, ILogger logger, CancellationToken ct)
    {
        await SendPingAsync(session, ct);
        ArmReping(session, logger);
    }

    private static Task SendPingAsync(ProudSession session, CancellationToken ct)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)Environment.TickCount64);
        return session.SendRmiAsync(ZoneS2COpcodes.ReqPing, body, ct);
    }

 /// <summary>
 /// Replaces the connection's pending re-ping with one due <see cref="RepingInterval"/> from now. The loop
 /// runs on its own token and ends when the connection closes or a newer 2593 restarts it.
 /// </summary>
    private static void ArmReping(ProudSession session, ILogger logger)
    {
        var timer = Repings.GetValue(session, s =>
        {
            var t = new RepingTimer();
            s.Disconnected += _ => { t.Stop(); return Task.CompletedTask; };
            return t;
        });
        var token = timer.Restart();
        _ = RepingLoopAsync(session, logger, token);
    }

    private static async Task RepingLoopAsync(ProudSession session, ILogger logger, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RepingInterval, token).ConfigureAwait(false);
                if (!session.IsConnected) return;
                await SendPingAsync(session, token).ConfigureAwait(false);
                logger.LogTrace("Session {Id}: re-ping 2593 after {Interval}", session.SessionId, RepingInterval);
            }
        }
        catch (OperationCanceledException)
        {
            
        }
        catch (Exception ex)
        {
            
            logger.LogDebug(ex, "Session {Id}: re-ping loop ended", session.SessionId);
        }
    }

 /// <summary>One connection's re-ping: the token source of the loop currently waiting, if any.</summary>
    private sealed class RepingTimer
    {
        private CancellationTokenSource? _cts;

        public CancellationToken Restart()
        {
            var next = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _cts, next);
            previous?.Cancel();
            previous?.Dispose();
            return next.Token;
        }

        public void Stop()
        {
            var previous = Interlocked.Exchange(ref _cts, null);
            previous?.Cancel();
            previous?.Dispose();
        }
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 8) return Task.CompletedTask;

        
        
        var token = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[..8]);
        var now = (ulong)Environment.TickCount64;
        if (token <= now)
            Logger.LogTrace("[Ping] {Name} answered keepalive {Token:X16}, round trip {Rtt} ms",
                player.Name, token, now - token);
        else
            
            
            Logger.LogTrace("[Ping] {Name} answered keepalive {Token:X16} ahead of our clock",
                player.Name, token);

        return Task.CompletedTask;
    }
}
