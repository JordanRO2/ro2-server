namespace RO2.Combats.Domain;

/// <summary>
/// One accepted skill start waiting to be consumed by a damage or tick packet. The group and level are the id's
/// own parts (skill id = group * 100 + level).
/// </summary>
public readonly record struct PendingCast(uint SkillId, uint Group, uint Level, uint TargetHandle,
    DateTime AcceptedAtUtc, DateTime ExpiresAtUtc);

/// <summary>
/// One caster's cast state: the skill groups whose accepted 1508 has not yet been followed by a 1509 (in flight),
/// the per-skill pending casts a 1509/1549 consumes, the per-cast 1549 tick ledger, and the open 1506 cast bars.
/// All four share one lock, so a caster's state is read and mutated atomically. The host holds one instance per
/// player (lifetime = the player; lost on reload) and computes the catalog-fed cast durations it passes in.
/// </summary>
public sealed class CastLedger
{
    /// <summary>Slack added to a skill's cast duration before an unconsumed pending cast or open cast bar lapses (7 s).</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(7);

    /// <summary>Shortest gap accepted between two 1549 ticks of one cast (100 ms); a real tick is never inside it.</summary>
    public static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Most ticks accepted for one cast (64): the grace over the shortest interval. Bounds tick damage, does not model it.</summary>
    public const int MaxTicksPerCast = 64;

    /// <summary>Most pending casts / cast bars kept per player; the oldest is dropped past it so a 1508 flood cannot grow the list.</summary>
    public const int MaxPendingPerPlayer = 32;

    private sealed class TickState { public int LastSequence = -1; public int Count; public DateTime LastAtUtc; }

    private readonly object _gate = new();
    private readonly HashSet<uint> _groups = new();
    private readonly List<PendingCast> _pending = new();
    private readonly Dictionary<uint, TickState> _ticks = new();
    private readonly Dictionary<uint, DateTime> _casting = new();

    /// <summary>A 1508 of <paramref name="skillGroup"/> was accepted: its cast is in flight until its 1509.</summary>
    public void BeginCast(uint skillGroup) { lock (_gate) _groups.Add(skillGroup); }

    /// <summary>The caster's 1509 for <paramref name="skillId"/> arrived: its group's cast has landed.</summary>
    public void MarkLanded(uint skillId) { lock (_gate) _groups.Remove(skillId / 100u); }

    /// <summary>True when a 1509 of <paramref name="skillGroup"/> has arrived since its last accepted 1508 (nothing of it in flight).</summary>
    public bool HasLanded(uint skillGroup) { lock (_gate) return !_groups.Contains(skillGroup); }

    /// <summary>Records an accepted 1508/1561 as a pending cast expiring at <paramref name="nowUtc"/> + <paramref name="life"/>.</summary>
    public void RecordPending(uint skillId, uint targetHandle, DateTime nowUtc, TimeSpan life)
    {
        var cast = new PendingCast(skillId, skillId / 100u, skillId % 100u, targetHandle, nowUtc, nowUtc + life);
        lock (_gate)
        {
            _pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            if (_pending.Count >= MaxPendingPerPlayer) _pending.RemoveAt(0);
            _pending.Add(cast);
            _ticks.Remove(skillId);   // a new cast of the skill starts a new tick run
        }
    }

    /// <summary>Consumes the oldest unexpired pending cast of exactly <paramref name="skillId"/> (and <paramref name="targetHandle"/> when given).</summary>
    public bool TryConsume(uint skillId, DateTime nowUtc, out PendingCast cast, uint? targetHandle = null)
        => TakePending(nowUtc, p => p.SkillId == skillId && (targetHandle is null || p.TargetHandle == targetHandle), out cast);

    /// <summary>As <see cref="TryConsume"/>, matching any level of <paramref name="skillGroup"/>.</summary>
    public bool TryConsumeGroup(uint skillGroup, DateTime nowUtc, out PendingCast cast)
        => TakePending(nowUtc, p => p.Group == skillGroup, out cast);

    /// <summary>
    /// The 1509/1549 gate: true with the governing cast when an unexpired pending cast of exactly
    /// <paramref name="skillId"/> exists. <paramref name="consume"/> removes it (a non-channelled 1509 spends its
    /// single cast); a channelled 1509 / over-time 1549 pass with consume false so the entry survives to its grace.
    /// </summary>
    public bool TryGovern(uint skillId, DateTime nowUtc, bool consume, out PendingCast cast)
    {
        cast = default;
        lock (_gate)
        {
            _pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            var i = _pending.FindIndex(p => p.SkillId == skillId);
            if (i < 0) return false;
            cast = _pending[i];
            if (consume) { _pending.RemoveAt(i); _ticks.Remove(skillId); }
            return true;
        }
    }

    /// <summary>Drops the expired pending casts; returns how many it removed.</summary>
    public int PruneExpired(DateTime nowUtc) { lock (_gate) return _pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc); }

    /// <summary>How many unexpired pending casts are held.</summary>
    public int PendingCount(DateTime nowUtc) { lock (_gate) return _pending.Count(p => p.ExpiresAtUtc > nowUtc); }

    private bool TakePending(DateTime nowUtc, Func<PendingCast, bool> match, out PendingCast cast)
    {
        cast = default;
        lock (_gate)
        {
            _pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            var i = _pending.FindIndex(p => match(p));
            if (i < 0) return false;
            cast = _pending[i];
            _pending.RemoveAt(i);
            _ticks.Remove(cast.SkillId);
            return true;
        }
    }

    /// <summary>Records an accepted 1506 casting start of <paramref name="skillId"/>, open until <paramref name="expiresAtUtc"/> (or a 1507/1508/1509).</summary>
    public void BeginCasting(uint skillId, DateTime nowUtc, DateTime expiresAtUtc)
    {
        lock (_gate)
        {
            foreach (var lapsed in _casting.Where(c => c.Value <= nowUtc).Select(c => c.Key).ToList())
                _casting.Remove(lapsed);
            if (_casting.Count >= MaxPendingPerPlayer && !_casting.ContainsKey(skillId))
                _casting.Remove(_casting.MinBy(c => c.Value).Key);
            _casting[skillId] = expiresAtUtc;
        }
    }

    /// <summary>True when <paramref name="skillId"/> has an open cast bar or an unexpired pending cast of that exact id.</summary>
    public bool IsCasting(uint skillId, DateTime nowUtc)
    {
        lock (_gate)
            return (_casting.TryGetValue(skillId, out var until) && until > nowUtc)
                   || _pending.Any(p => p.SkillId == skillId && p.ExpiresAtUtc > nowUtc);
    }

    /// <summary>Closes <paramref name="skillId"/>'s cast bar (a 1507 cancelled it); its pending cast, if any, is left to its own 1509/expiry.</summary>
    public void EndCasting(uint skillId) { lock (_gate) _casting.Remove(skillId); }

    /// <summary>
    /// Admits one 1549 tick of <paramref name="skillId"/>'s current cast, or refuses it: the first tick of a cast is
    /// accepted with any sequence; after that the sequence byte must DIFFER from the last accepted one, at least
    /// <see cref="MinTickInterval"/> must have passed, and fewer than <see cref="MaxTicksPerCast"/> accepted. A
    /// refused tick changes nothing.
    /// </summary>
    public bool TryAcceptTick(uint skillId, byte sequence, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_ticks.TryGetValue(skillId, out var ledger))
            {
                if (_ticks.Count >= MaxPendingPerPlayer)
                {
                    foreach (var stale in _ticks.Where(t => nowUtc - t.Value.LastAtUtc > PendingGrace * 2)
                                 .Select(t => t.Key).ToList())
                        _ticks.Remove(stale);
                    if (_ticks.Count >= MaxPendingPerPlayer) return false;
                }
                _ticks[skillId] = new TickState { LastSequence = sequence, Count = 1, LastAtUtc = nowUtc };
                return true;
            }
            if (sequence == ledger.LastSequence || ledger.Count >= MaxTicksPerCast || nowUtc - ledger.LastAtUtc < MinTickInterval)
                return false;
            ledger.LastSequence = sequence;
            ledger.Count++;
            ledger.LastAtUtc = nowUtc;
            return true;
        }
    }

    /// <summary>Starts <paramref name="skillId"/>'s tick run over (an accepted 1509 of it arrived).</summary>
    public void ResetTicks(uint skillId) { lock (_gate) _ticks.Remove(skillId); }
}
