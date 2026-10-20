using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Background service that regenerates HP/MP for all players every second and reflects the new bars with
/// 2401 OnActorAutoHeal, to each player and to the players that see it (see <see cref="ActorVitalsBroadcast"/>). (Saving the result is not done here: the Hp/Mp setters mark the
/// player dirty and <see cref="PersistenceService"/> writes it behind.)
/// </summary>
/// <remarks>
/// <para>
/// FAILURE ISOLATION. An exception that escapes <see cref="ExecuteAsync"/> faults this BackgroundService,
/// and the host's default <c>BackgroundServiceExceptionBehavior.StopHost</c> then stops the whole Zone host
/// — and on Hosting 10 the process still exits with code 0, so it looks like a clean shutdown. The per-player
/// send used to be unguarded, so a single half-closed socket, or a session whose send lock a concurrent
/// disconnect had just disposed, took every player's regen and the Zone down with it.
/// </para>
/// <para>
/// So each player's step is caught and logged on its own (<see cref="RegenTickAsync"/>, as
/// <see cref="ServerTimeService"/> does per session), and a tick that still fails is logged and the next one
/// runs (<see cref="RunAsync"/>). Only cancellation of the stopping token ends the loop. The regen math and
/// the delay-then-tick timing are unchanged.
/// </para>
/// </remarks>
public class RegenService : BackgroundService
{
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<RegenService> _logger;

    // Out-of-combat regen, reflected with 2401 OnActorAutoHeal. The interval is 1 s. The per-tick amount is a
    // flat percentage of the maximum (about 0.30% of MaxHp and 0.24% of MaxSp) with a floor of 1; the exact
    // stat/level formula behind that amount is not carried by 2401, so it is approximated this way.
    private const int RegenIntervalMs = 1000;    // 1 second
    private const float HpRegenPercent = 0.003f; // ~0.3% of MaxHp per 1s tick
    private const float MpRegenPercent = 0.0024f; // ~0.24% of MaxSp per 1s tick

    public RegenService(PlayerStateManager playerManager, BroadcastService broadcast, ILogger<RegenService> logger)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HP/MP regeneration service started (interval: {Ms}ms)", RegenIntervalMs);

        await RunAsync(RegenIntervalMs,
            ct => RegenTickAsync(_playerManager.GetAllPlayers(), SendAutoHealAsync, ct),
            stoppingToken);
    }

    /// <summary>
    /// The service loop: wait one interval, run one tick, until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// The interval and the tick are parameters only so the survive-a-failed-tick contract can be tested
    /// without sockets or a 1 s wait; production passes <see cref="RegenIntervalMs"/> and
    /// <see cref="RegenTickAsync"/>.
    /// </remarks>
    internal async Task RunAsync(int intervalMs, Func<CancellationToken, Task> tick, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Delay first, then work, exactly as before. Cancellation surfaces here as an
            // OperationCanceledException, which completes ExecuteAsync as canceled: the host's normal stop,
            // not a fault.
            await Task.Delay(intervalMs, stoppingToken);

            try
            {
                await tick(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Anything but a stop is a bug in this tick, not a reason to take the Zone down (see remarks):
                // log it and run the next tick on schedule. Nothing is lost by skipping the rest of a tick,
                // because a 2401 carries the current bars, not a delta, so the next one repairs the client.
                _logger.LogError(ex, "HP/MP regen tick failed; continuing with the next tick");
            }
        }
    }

    /// <summary>
    /// One regen tick over <paramref name="players"/>: applies the regen to each and, where HP or SP moved,
    /// hands the player to <paramref name="sendAutoHeal"/>. Returns how many players' step failed.
    /// </summary>
    /// <remarks>
    /// Each player is isolated: a failure (in practice the send, see the class remarks) is logged for that
    /// session and the tick goes on to the next player, so one bad session never stops regen for the others.
    /// A stop request is not swallowed, so the loop still ends promptly.
    /// </remarks>
    internal async Task<int> RegenTickAsync(IEnumerable<PlayerState> players, Func<PlayerState, Task> sendAutoHeal,
        CancellationToken stoppingToken)
    {
        var failed = 0;
        foreach (var player in players)
        {
            try
            {
                if (ApplyRegen(player))
                    await sendAutoHeal(player);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Debug, like ServerTimeService: a session dying mid-send is routine, and it does not repeat
                // every second — a failed send leaves the socket not Connected (or the session is already
                // disposed), so the next tick's send sees IsConnected false and returns without sending.
                failed++;
                _logger.LogDebug(ex, "HP/MP regen failed for session {Id}", player.SessionId);
            }
        }
        return failed;
    }

    /// <summary>
    /// Applies one tick of out-of-combat regen to <paramref name="player"/> and reports whether HP or SP
    /// moved, which is the only case a 2401 is sent for.
    /// </summary>
    internal static bool ApplyRegen(PlayerState player)
    {
        // Under the vitals lock, so a regen tick cannot heal a player a combat/fall/DoT hit has just killed off
        // another thread — which would silently resurrect the corpse. A dead player (IsDead or HP 0) does not regen.
        lock (player.Vitals)
        {
            if (player.IsDead || player.Hp <= 0) return false;

            var hpBefore = player.Hp;
            var mpBefore = player.Mp;

            if (player.Hp < player.MaxHp)
                player.ApplyHpDelta(Math.Max(1, (int)(player.MaxHp * HpRegenPercent)));

            if (player.Mp < player.MaxMp)
                player.ApplySpDelta(Math.Max(1, (int)(player.MaxMp * MpRegenPercent)));

            // Only send update if values changed
            return player.Hp != hpBefore || player.Mp != mpBefore;
        }
    }

    /// <summary>
    /// Sends the player's current bars as 2401 OnActorAutoHeal to the player and to everyone on its map
    /// (<see cref="ActorVitalsBroadcast"/>), so onlookers see a PC's bars refill too. A player whose
    /// session is gone is skipped, as before.
    /// </summary>
    private async Task SendAutoHealAsync(PlayerState player)
    {
        // Envelope, handle and field ORDER: see ActorVitalsPackets.BuildActorAutoHeal2401. A raw body keyed by
        // SessionId with cur/max interleaved crashes the client on the first regen tick after any damage.
        if (_broadcast.GetSession(player.SessionId) != null)
            await ActorVitalsBroadcast.SendAutoHealAsync(_broadcast, player);
    }
}
