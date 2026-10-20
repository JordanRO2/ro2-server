using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Drives a player's "in battle" combat stance: the 2398 OnChangeState the client turns into the weapon-drawn
/// pose and the "you have entered battle" transition. The server owns it; without it a player never enters battle
/// mode (the only PC state this server used to send was a per-hit <see cref="CombatPackets.ActorState.PcNormal"/>,
/// which cancelled the client's own predicted stance — the stuck-run bug — and was removed).
/// </summary>
/// <remarks>
/// <para>
/// A PC enters the combat band (state 0x0C..0x14) on its first normal attack given or first hit taken (or a
/// skill-cast open). The value is a per-character constant, almost always
/// <see cref="CombatPackets.ActorState.PcCombat"/> 0x0C, and it re-pulses on each combat action rather than
/// latching once. It returns to <see cref="CombatPackets.ActorState.PcNormal"/> only after an out-of-combat
/// timeout.
/// </para>
/// <para>
/// So: <see cref="MarkCombat"/> is called wherever the player attacks or is hit; it enters battle mode (and
/// re-pulses 0x0C, throttled to <see cref="RepulseInterval"/>) and stamps the last action; the tick drops a
/// player back to normal <see cref="LeaveTimeout"/> after its last action. The stance 2398 goes to the whole map
/// (the attacker included, since the client applies it to the local avatar too, which is what draws the weapon).
/// Weapon-specific minority stances (0x0D/0x0E/0x11/0x13) are not modelled; 0x0C is sent for every weapon.
/// </para>
/// </remarks>
public sealed class PcCombatStateService : ITickable
{
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<PcCombatStateService> _logger;

    /// <summary>How long after a player's last combat action it drops out of battle mode.</summary>
    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Minimum gap between two 0x0C re-pulses for one player, so a fast fight does not flood the map.</summary>
    private static readonly TimeSpan RepulseInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>How often the leave sweep runs, independent of the loop's tick rate.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Cap on the backlog the sweep accumulates while a previous sweep is still running, in
    /// <see cref="Interval"/> units, so a slow sweep does not trigger a burst of catch-up sweeps.</summary>
    private const int MaxBacklogIntervals = 4;

    /// <summary>2398 combat flag (body offset +14) of the frame that ENTERS battle mode. The client's 2398 handler
    /// branches on this byte; the 1 drives the combat-mode entry (the "Battle Start" message and combat music).</summary>
    private const byte CombatFlagEnter = 1;

    /// <summary>2398 combat flag (body offset +14) of a re-pulse or a return to normal: 0.</summary>
    private const byte CombatFlagNone = 0;

    /// <summary>2398 target handle for a PC stance change: none (0).</summary>
    private const uint NoTargetHandle = 0;

    /// <summary>2398 trailing field (body offset +19) for a PC stance change: 0.</summary>
    private const uint NoTrailingValue = 0;

    private double _accum;
    private int _running;

    public string Name => "PcCombatState";

    public PcCombatStateService(PlayerStateManager players, BroadcastService broadcast,
        ILogger<PcCombatStateService> logger)
    {
        _players = players;
        _broadcast = broadcast;
        _logger = logger;
    }

    /// <summary>
    /// Records a combat action by <paramref name="player"/> (it attacked or was hit) and keeps it in battle mode:
    /// enters the stance on the first action and re-pulses 0x0C on later ones (throttled). Call it from the
    /// player's attack, skill-cast and damage-taken paths. Cheap and fire-and-forget; safe to call per hit.
    /// </summary>
    public void MarkCombat(PlayerState? player)
    {
        if (player is null || player.IsDead)
            return;

        var now = DateTime.UtcNow;
        player.LastCombatActionAt = now;

        var entering = !player.InBattleMode;
        if (entering || now - player.LastBattleStateSentAt >= RepulseInterval)
        {
            player.InBattleMode = true;
            player.LastBattleStateSentAt = now;
            // The enter frame carries blob+14 = 1; re-pulses carry 0. The client's 2398 handler branches on
            // blob+14, and the enter's 1 is what drives the combat-mode entry ("Battle Start" message and combat BGM).
            _ = SendStateAsync(player, CombatPackets.ActorState.PcCombat, entering ? CombatFlagEnter : CombatFlagNone);
        }
    }

    public void Tick(double deltaSeconds)
    {
        _accum += deltaSeconds;
        double interval = Interval.TotalSeconds;
        if (_accum < interval)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (_accum > interval * MaxBacklogIntervals) _accum = interval * MaxBacklogIntervals;
            return;
        }
        _accum = 0;
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var player in _players.GetAllPlayers())
            {
                if (!player.InBattleMode)
                    continue;
                // A dead player's state is owned by the death sequence; just clear the flag so a revival re-enters
                // cleanly. A live player drops out of the stance once it has gone quiet for the timeout.
                if (player.IsDead)
                {
                    player.InBattleMode = false;
                    continue;
                }
                if (now - player.LastCombatActionAt >= LeaveTimeout)
                {
                    player.InBattleMode = false;
                    await SendStateAsync(player, CombatPackets.ActorState.PcNormal, CombatFlagNone);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PC combat-state tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Broadcasts a PC-subject 2398 OnChangeState for <paramref name="player"/> to its map (the client
    /// applies it to the local avatar too). BuildChangeState2398 mirrors blob+4==blob+8 for a PC handle and
    /// sets blob+14 from the combat flag.</summary>
    private async Task SendStateAsync(PlayerState player, byte state, byte combatFlag)
    {
        try
        {
            await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeState,
                w => w.WriteBytes(CombatPackets.BuildChangeState2398(
                    player.EntityId, state, NoTargetHandle, NoTrailingValue, combatFlag)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending PC combat-state 2398 (state {State}) for {Name} failed", state, player.Name);
        }
    }
}
