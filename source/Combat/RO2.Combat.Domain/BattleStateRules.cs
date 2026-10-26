namespace RO2.Combats.Domain;

/// <summary>
/// Pure timing rules of a PC's "in battle" combat stance (the 2398 OnChangeState 0x0C band): when a combat
/// action enters the stance, when it re-pulses it, and when a quiet player drops out. The host owns the state
/// (PlayerState flags), the wall clock and the 2398 broadcast; these rules only decide.
/// </summary>
public static class BattleStateRules
{
    /// <summary>How long after a player's last combat action it drops out of battle mode.</summary>
    public static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Minimum gap between two 0x0C re-pulses for one player, so a fast fight does not flood the map.</summary>
    public static readonly TimeSpan RepulseInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>What a combat action should send: nothing, the entering frame (blob+14 = 1), or a throttled re-pulse (0).</summary>
    public enum Pulse
    {
        /// <summary>Already in battle mode and inside the re-pulse interval: send nothing.</summary>
        None,

        /// <summary>Not in battle mode: enter it (the "Battle Start" frame, blob+14 = 1).</summary>
        Enter,

        /// <summary>In battle mode and past the re-pulse interval: re-pulse 0x0C (blob+14 = 0).</summary>
        Repulse,
    }

    /// <summary>
    /// The 2398 a combat action should send: <see cref="Pulse.Enter"/> when the player is not yet in battle mode,
    /// else <see cref="Pulse.Repulse"/> once <see cref="RepulseInterval"/> has passed since the last frame sent,
    /// else <see cref="Pulse.None"/>.
    /// </summary>
    public static Pulse OnCombatAction(bool inBattleMode, DateTime lastSentAtUtc, DateTime nowUtc)
    {
        if (!inBattleMode)
            return Pulse.Enter;
        return nowUtc - lastSentAtUtc >= RepulseInterval ? Pulse.Repulse : Pulse.None;
    }

    /// <summary>
    /// True when a (live) player in battle mode has gone quiet for <see cref="LeaveTimeout"/> since its last
    /// combat action and should drop back to the normal stance. A dead player is handled by the caller.
    /// </summary>
    public static bool ShouldLeave(DateTime lastCombatActionAtUtc, DateTime nowUtc)
        => nowUtc - lastCombatActionAtUtc >= LeaveTimeout;
}
