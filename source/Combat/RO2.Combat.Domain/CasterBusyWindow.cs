namespace RO2.Combats.Domain;

/// <summary>
/// One caster's last accepted cast: when it began and how long it lasts (its Casting_Time + Excution_Time).
/// A cast refused on cooldown is "idle" once more than that duration has passed since the start, so the host
/// can tell a too-soon recast apart from one sent with no cast outstanding. The host holds one instance per
/// player (lifetime = the player; lost on reload) and feeds in the catalog-fed durations.
/// </summary>
public sealed class CasterBusyWindow
{
    private readonly object _gate = new();
    private DateTime _startedAt;
    private TimeSpan _duration;
    private bool _recorded;

    /// <summary>Stores the caster's last accepted cast: its start and its Casting_Time + Excution_Time.</summary>
    public void Record(DateTime startedAtUtc, TimeSpan duration)
    {
        lock (_gate)
        {
            _startedAt = startedAtUtc;
            _duration = duration;
            _recorded = true;
        }
    }

    /// <summary>
    /// True when the caster's last accepted cast began more than its duration before <paramref name="nowUtc"/>,
    /// or when there is none.
    /// </summary>
    public bool IsIdle(DateTime nowUtc)
    {
        lock (_gate)
        {
            return !_recorded || nowUtc - _startedAt > _duration;
        }
    }
}
