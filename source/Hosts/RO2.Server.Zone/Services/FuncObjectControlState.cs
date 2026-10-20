using System.Diagnostics;
using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>The function-object control a player has running: its target and the state broadcast at its start.</summary>
/// <param name="Target">The 1410 target handle (0x14 function object or 0x13 NPC), echoed by the 2515.</param>
/// <param name="State">The 2398 state byte the start broadcast, or null when it sent no 2398.</param>
public readonly record struct FuncObjectControl(uint Target, byte? State);

/// <summary>What <see cref="FuncObjectControlState.TryEndAfter"/> did with the control it was asked to end.</summary>
public enum FuncObjectControlEnd
{
 /// <summary>The control on that target was running and had run long enough: it is ended and returned.</summary>
    Ended,

 /// <summary>No control on that target is running (none began, another target's is, or it already ended).</summary>
    NotRunning,

 /// <summary>The control on that target is running but began less than the required time ago: it keeps running.</summary>
    TooEarly,
}

/// <summary>
/// Tracks the one running function-object control per player. It is stored beside the <see cref="PlayerState"/>
/// in a ConditionalWeakTable, so it is released with the player.
/// </summary>
/// <remarks>
/// A control starts on a 1410 request and ends on the matching 1411 or 1412 for the same target. A 1411 or 1412
/// for another target leaves the running control alone.
/// </remarks>
public static class FuncObjectControlState
{
    private sealed class Slot
    {
        public FuncObjectControl? Control;

 /// <summary><see cref="Stopwatch.GetTimestamp"/> when <see cref="Control"/> began.</summary>
        public long BeganAt;
    }

    private static readonly ConditionalWeakTable<PlayerState, Slot> Controls = new();

 /// <summary>
 /// Sets <paramref name="control"/> as the player's running control, replacing any previous one, and records
 /// when it began.
 /// </summary>
    public static void Begin(PlayerState player, FuncObjectControl control)
    {
        var slot = Controls.GetValue(player, static _ => new Slot());
        var now = Stopwatch.GetTimestamp();
        lock (slot)
        {
            slot.Control = control;
            slot.BeganAt = now;
        }
    }

 /// <summary>
 /// Ends the running control when it is on <paramref name="target"/> and began at least
 /// <paramref name="minElapsed"/> before <paramref name="now"/>, and returns it. A control on that target that
 /// began more recently keeps running and yields <see cref="FuncObjectControlEnd.TooEarly"/>.
 /// </summary>
    public static FuncObjectControlEnd TryEndAfter(PlayerState player, uint target, TimeSpan minElapsed, long now,
        out FuncObjectControl control, out TimeSpan elapsed)
    {
        control = default;
        elapsed = TimeSpan.Zero;
        if (!Controls.TryGetValue(player, out var slot)) return FuncObjectControlEnd.NotRunning;
        lock (slot)
        {
            if (slot.Control is not { } running || running.Target != target) return FuncObjectControlEnd.NotRunning;
            elapsed = Stopwatch.GetElapsedTime(slot.BeganAt, now);
            if (elapsed < minElapsed) return FuncObjectControlEnd.TooEarly;
            control = running;
            slot.Control = null;
            return FuncObjectControlEnd.Ended;
        }
    }

 /// <summary>
 /// Ends the running control when it is on <paramref name="target"/> and returns it. Returns false, and leaves
 /// any running control in place, when no control on that target is running.
 /// </summary>
    public static bool TryEnd(PlayerState player, uint target, out FuncObjectControl control)
    {
        control = default;
        if (!Controls.TryGetValue(player, out var slot)) return false;
        lock (slot)
        {
            if (slot.Control is not { } running || running.Target != target) return false;
            control = running;
            slot.Control = null;
            return true;
        }
    }

 /// <summary>The player's running control, or null (for tests and diagnostics; the handlers go through TryEnd).</summary>
    internal static FuncObjectControl? Current(PlayerState player)
    {
        if (!Controls.TryGetValue(player, out var slot)) return null;
        lock (slot) return slot.Control;
    }
}
