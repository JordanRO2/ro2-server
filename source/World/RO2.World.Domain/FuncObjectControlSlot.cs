using System.Diagnostics;

namespace RO2.Worlds.Domain;

/// <summary>The function-object control a player has running: its target and the state broadcast at its start.</summary>
/// <param name="Target">The 1410 target handle (0x14 function object or 0x13 NPC), echoed by the 2515.</param>
/// <param name="State">The 2398 state byte the start broadcast, or null when it sent no 2398.</param>
public readonly record struct FuncObjectControl(uint Target, byte? State);

/// <summary>What <see cref="FuncObjectControlSlot.TryEndAfter"/> did with the control it was asked to end.</summary>
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
/// The one function-object control a player has running, and the rules that begin and end it. A control starts on a 1410
/// request and ends on the matching 1411 or 1412 for the same target; a 1411 or 1412 for another target leaves the running
/// control alone. The slot is serialised by its own lock; "now" is a <see cref="Stopwatch.GetTimestamp"/> value the host
/// reads. Which player owns which slot is the host's registry.
/// </summary>
public sealed class FuncObjectControlSlot
{
    private readonly Lock _gate = new();
    private FuncObjectControl? _control;
    private long _beganAt;

    /// <summary>The running control, or null.</summary>
    public FuncObjectControl? Current
    {
        get { lock (_gate) return _control; }
    }

    /// <summary>
    /// Sets <paramref name="control"/> as the running control, replacing any previous one, and records
    /// <paramref name="now"/> as when it began.
    /// </summary>
    public void Begin(FuncObjectControl control, long now)
    {
        lock (_gate)
        {
            _control = control;
            _beganAt = now;
        }
    }

    /// <summary>
    /// Ends the running control when it is on <paramref name="target"/> and began at least
    /// <paramref name="minElapsed"/> before <paramref name="now"/>, and returns it. A control on that target that began more
    /// recently keeps running and yields <see cref="FuncObjectControlEnd.TooEarly"/>.
    /// </summary>
    public FuncObjectControlEnd TryEndAfter(uint target, TimeSpan minElapsed, long now,
        out FuncObjectControl control, out TimeSpan elapsed)
    {
        control = default;
        elapsed = TimeSpan.Zero;
        lock (_gate)
        {
            if (_control is not { } running || running.Target != target) return FuncObjectControlEnd.NotRunning;
            elapsed = Stopwatch.GetElapsedTime(_beganAt, now);
            if (elapsed < minElapsed) return FuncObjectControlEnd.TooEarly;
            control = running;
            _control = null;
            return FuncObjectControlEnd.Ended;
        }
    }

    /// <summary>
    /// Ends the running control when it is on <paramref name="target"/> and returns it. Returns false, and leaves any running
    /// control in place, when no control on that target is running.
    /// </summary>
    public bool TryEnd(uint target, out FuncObjectControl control)
    {
        control = default;
        lock (_gate)
        {
            if (_control is not { } running || running.Target != target) return false;
            control = running;
            _control = null;
            return true;
        }
    }
}
