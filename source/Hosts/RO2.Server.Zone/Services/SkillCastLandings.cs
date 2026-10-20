using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Per PC, the skill groups (skill id / 100) whose accepted 1508 ReqSkillStart has NOT yet been followed by the
/// caster's own 1509 ReqSkillDamage. Written by SkillStartHandler (put in flight when it accepts a 1508) and by
/// SkillDamageHandler (marked by every 1509), read by SkillStartHandler to answer a burst duplicate.
/// </summary>
/// <remarks>
/// <para>
/// WHY. One activation sends two or more 1508 frames within a few hundred ms, and the server answers the
/// copies differently depending on whether the cast's 1509 came in between. A copy is a same-skill 1508 that
/// follows an accepted one by under 350 ms while the cooldown that acceptance's 2715 armed is still running:
/// </para>
/// <list type="bullet">
/// <item>with the caster's 1509 between the two: the copy is refused (a 20152/22900/22303 2715) or gets no
/// 2715 at all;</item>
/// <item>with no 1509 between: the copy gets no 2715 at all, or is refused.</item>
/// </list>
/// <para>
/// A copy is never answered with a second accepting 2715, and the burst carries at most one 2716 of the
/// caster. So the rule is "refuse when a 1509 has come, say nothing when it has not"; accepting the copies
/// with no 1509 would add a second 2715 and 2716. Copies arrive under 350 ms after the accepted frame in
/// both cases, so no time window can stand in for the 1509.
/// </para>
/// <para>
/// Kept per PlayerState in a <see cref="ConditionalWeakTable{TKey,TValue}"/>, as <see cref="SkillTickTargets"/> is,
/// so it leaves with the player and needs no DI registration. Runtime only: a relog starts with nothing in flight.
/// </para>
/// </remarks>
public static class SkillCastLandings
{
    /// <summary>The groups with an accepted 1508 and no 1509 since.</summary>
    private sealed class InFlight
    {
        public readonly HashSet<uint> Groups = new();

        /// <summary>SP0-CASTSTATE: accepted casts waiting for their one 1509/1549, oldest first. Guarded by the
        /// InFlight instance's own lock, like <see cref="Groups"/>.</summary>
        public readonly List<PendingCast> Pending = new();

        /// <summary>CB-H3: the 1549 ticks accepted for each skill id's current cast. Guarded by the InFlight instance's lock.</summary>
        public readonly Dictionary<uint, TickLedger> Ticks = new();

        /// <summary>CB-H7: the skill ids whose 1506 casting start was accepted and has not ended, with when each
        /// lapses. Guarded by the InFlight instance's lock.</summary>
        public readonly Dictionary<uint, DateTime> Casting = new();
    }

    /// <summary>The ticks accepted so far for one cast: the last sequence byte, how many, and when the last came.</summary>
    private sealed class TickLedger
    {
        public int LastSequence = -1;
        public int Count;
        public DateTime LastAtUtc;
    }

    /// <summary>
    /// One accepted skill start waiting to be consumed by a damage or tick packet (SP0-CASTSTATE). The group and the
    /// level are the id's own parts (skill id = group * 100 + level, as <see cref="MarkLanded"/> reads it).
    /// </summary>
    public readonly record struct PendingCast(uint SkillId, uint Group, uint Level, uint TargetHandle,
        DateTime AcceptedAtUtc, DateTime ExpiresAtUtc);

    /// <summary>
    /// Slack added to the skill's Casting_Time + Excution_Time before an unconsumed pending cast expires.
    /// The gap from a start to its 1509 is covered by 7 s (a channelled skill's longest is just under 7 s).
    /// Casting_Time is not part of that gap (it is client to server latency).
    /// </summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(7);

    // NOTES (the wiring they describe is in place: see the callers of RecordPending and TryGovern):
    //  - The pending model is PER SKILL: InFlight.Pending is a list matched by skillId (TryConsume) or by
    //    group (TryConsumeGroup), never a single "last 1508" slot, because a 1509 does not always belong to the
    //    most recent start (item-skill 1561 starts interleave). Both are recorded: the first frame of every
    //    non-refused 1508 (burst copies excluded) and every 1561.
    //  - CHANNELLED skills (Skill_Duration > 0) emit no 1549 ticks: the channel is repeated 1509s, up to ~7 s
    //    after the start. Do NOT clear the pending cast on the first 1509 of such a skill; it must stay until
    //    the grace expires.
    //  - The real 1549 tick interval is a per-skill constant of roughly 0.11-0.65 s. It is NOT the table
    //    Tick_Interval, and the tick count is NOT derivable from Number_Of_Normal_Influences. Do not derive
    //    either. Most 1549 runs are a single tick.

    /// <summary>
    /// Shortest gap accepted between two 1549 ticks of one cast (CB-H3). A conservative floor under the
    /// 0.11-0.65 s per-skill tick interval noted above; it exists to stop a client firing
    /// ticks back to back, and a real tick is never inside it.
    /// </summary>
    public static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Most ticks accepted for one cast (CB-H3). There is no per-skill tick count in the
    /// data (the notes above rule out Number_Of_Normal_Influences), so this is the pending grace
    /// (<see cref="PendingGrace"/>, 7 s) over the shortest interval (0.11 s), rounded down. It bounds a cast's
    /// tick damage; it does not model it.
    /// </summary>
    public const int MaxTicksPerCast = 64;

    /// <summary>Most pending casts kept per player; the oldest is dropped past it so a 1508 flood cannot grow the list.</summary>
    public const int MaxPendingPerPlayer = 32;

    private static readonly ConditionalWeakTable<PlayerState, InFlight> Store = new();

    /// <summary>A 1508 of <paramref name="skillGroup"/> was accepted: its cast is in flight until its 1509.</summary>
    public static void BeginCast(PlayerState player, uint skillGroup)
    {
        var flight = Store.GetValue(player, _ => new InFlight());
        lock (flight) flight.Groups.Add(skillGroup);
    }

    /// <summary>The caster's 1509 for <paramref name="skillId"/> arrived: its group's cast has landed.</summary>
    public static void MarkLanded(PlayerState player, uint skillId)
    {
        if (!Store.TryGetValue(player, out var flight)) return;
        lock (flight) flight.Groups.Remove(skillId / 100u);
    }

    /// <summary>
    /// True when a 1509 of <paramref name="skillGroup"/> has arrived since its last accepted 1508, and also when the
    /// group was never accepted in this session (nothing of it is in flight).
    /// </summary>
    public static bool HasLanded(PlayerState player, uint skillGroup)
    {
        if (!Store.TryGetValue(player, out var flight)) return true;
        lock (flight) return !flight.Groups.Contains(skillGroup);
    }

    // ---- SP0-CASTSTATE: pending-cast ledger. Wired: RecordPending from the 1508/1561 start handlers, TryGovern from the 1509/1549 damage handlers. ------

    /// <summary>
    /// Records an accepted 1508 as a pending cast. It expires at <paramref name="nowUtc"/> plus
    /// <paramref name="lifetime"/>, or by default the skill's Casting_Time + Excution_Time
    /// (<see cref="SkillCasterBusyWindow.CastDuration"/>) plus <see cref="PendingGrace"/>.
    /// </summary>
    internal static void RecordPending(PlayerState player, uint skillId, uint targetHandle, DateTime nowUtc,
        TimeSpan? lifetime = null)
    {
        var life = lifetime ?? SkillCasterBusyWindow.CastDuration(skillId) + PendingGrace;
        var cast = new PendingCast(skillId, skillId / 100u, skillId % 100u, targetHandle, nowUtc, nowUtc + life);
        var flight = Store.GetValue(player, _ => new InFlight());
        lock (flight)
        {
            flight.Pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            if (flight.Pending.Count >= MaxPendingPerPlayer) flight.Pending.RemoveAt(0);
            flight.Pending.Add(cast);
            flight.Ticks.Remove(skillId);   // a new cast of the skill starts a new tick run
        }
    }

    /// <summary>
    /// Consumes the oldest unexpired pending cast of exactly <paramref name="skillId"/> (and of
    /// <paramref name="targetHandle"/> when given): true and the cast when one matched, false when none did, so a
    /// damage packet with no accepted 1508 behind it, or a second one for the same cast, is refused. Atomic per player.
    /// </summary>
    internal static bool TryConsume(PlayerState player, uint skillId, DateTime nowUtc, out PendingCast cast,
        uint? targetHandle = null)
        => TakePending(player, nowUtc, p => p.SkillId == skillId && (targetHandle is null || p.TargetHandle == targetHandle),
            out cast);

    /// <summary>As <see cref="TryConsume"/>, matching any level of <paramref name="skillGroup"/> (for a packet that
    /// carries the group, not the full id).</summary>
    internal static bool TryConsumeGroup(PlayerState player, uint skillGroup, DateTime nowUtc, out PendingCast cast)
        => TakePending(player, nowUtc, p => p.Group == skillGroup, out cast);

    /// <summary>
    /// The 1509/1549 damage gate (SP0-CASTSTATE): true, with the governing cast, when an unexpired pending cast of
    /// exactly <paramref name="skillId"/> exists — an accepted 1508 or 1561 recorded it within the grace.
    /// <paramref name="consume"/> removes the entry (a non-channelled 1509 spends its single cast); a channelled
    /// 1509's repeated frames and an over-time 1549's repeated ticks pass with <paramref name="consume"/> false so
    /// the entry survives until its grace expires (do not clear on the first 1509 of a
    /// channelled skill). False — no accepted start behind the packet — refuses it. Atomic per player.
    /// </summary>
    internal static bool TryGovern(PlayerState player, uint skillId, DateTime nowUtc, bool consume, out PendingCast cast)
    {
        cast = default;
        if (!Store.TryGetValue(player, out var flight)) return false;
        lock (flight)
        {
            flight.Pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            var i = flight.Pending.FindIndex(p => p.SkillId == skillId);
            if (i < 0) return false;
            cast = flight.Pending[i];
            if (consume)
            {
                flight.Pending.RemoveAt(i);
                flight.Ticks.Remove(skillId);   // the cast ended: its tick run with it
            }
            return true;
        }
    }

    /// <summary>Drops the player's expired pending casts; returns how many it removed.</summary>
    internal static int PruneExpired(PlayerState player, DateTime nowUtc)
    {
        if (!Store.TryGetValue(player, out var flight)) return 0;
        lock (flight) return flight.Pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
    }

    /// <summary>How many unexpired pending casts the player holds.</summary>
    internal static int PendingCount(PlayerState player, DateTime nowUtc)
    {
        if (!Store.TryGetValue(player, out var flight)) return 0;
        lock (flight) return flight.Pending.Count(p => p.ExpiresAtUtc > nowUtc);
    }

    private static bool TakePending(PlayerState player, DateTime nowUtc, Func<PendingCast, bool> match,
        out PendingCast cast)
    {
        cast = default;
        if (!Store.TryGetValue(player, out var flight)) return false;
        lock (flight)
        {
            flight.Pending.RemoveAll(p => p.ExpiresAtUtc <= nowUtc);
            var i = flight.Pending.FindIndex(p => match(p));
            if (i < 0) return false;
            cast = flight.Pending[i];
            flight.Pending.RemoveAt(i);
            flight.Ticks.Remove(cast.SkillId);
            return true;
        }
    }

    // ---- CB-H7: the casting bar (1506 start, 1507 cancel). ---------------------------------------------------------------

    /// <summary>
    /// Records an accepted 1506 casting start of <paramref name="skillId"/>: its cast bar is open until a 1507 ends it,
    /// its 1508 / 1509 follow, or its Casting_Time + Excution_Time + <see cref="PendingGrace"/> lapses.
    /// </summary>
    internal static void BeginCasting(PlayerState player, uint skillId, DateTime nowUtc)
    {
        var expiresAt = nowUtc + SkillCasterBusyWindow.CastDuration(skillId) + PendingGrace;
        var flight = Store.GetValue(player, _ => new InFlight());
        lock (flight)
        {
            foreach (var lapsed in flight.Casting.Where(c => c.Value <= nowUtc).Select(c => c.Key).ToList())
                flight.Casting.Remove(lapsed);
            if (flight.Casting.Count >= MaxPendingPerPlayer && !flight.Casting.ContainsKey(skillId))
                flight.Casting.Remove(flight.Casting.MinBy(c => c.Value).Key);
            flight.Casting[skillId] = expiresAt;
        }
    }

    /// <summary>
    /// True when <paramref name="skillId"/> has an open cast bar (<see cref="BeginCasting"/>) or an unexpired pending
    /// cast of exactly that id (an accepted 1508 / 1561), i.e. a 1507 cancel of it has something to cancel.
    /// </summary>
    internal static bool IsCasting(PlayerState player, uint skillId, DateTime nowUtc)
    {
        if (!Store.TryGetValue(player, out var flight)) return false;
        lock (flight)
            return (flight.Casting.TryGetValue(skillId, out var until) && until > nowUtc)
                   || flight.Pending.Any(p => p.SkillId == skillId && p.ExpiresAtUtc > nowUtc);
    }

    /// <summary>Closes <paramref name="skillId"/>'s cast bar (a 1507 cancelled it). Its pending cast, if any, is left to
    /// its own 1509 / expiry: a cancel never forgives the SP and cooldown the 1508 already took.</summary>
    internal static void EndCasting(PlayerState player, uint skillId)
    {
        if (!Store.TryGetValue(player, out var flight)) return;
        lock (flight) flight.Casting.Remove(skillId);
    }

    // ---- CB-H3: per-cast 1549 tick ledger. Wired: TryAcceptTick from SkillDamageTickHandler, ResetTicks from the accepted 1509. ----

    /// <summary>
    /// Admits one 1549 tick of <paramref name="skillId"/>'s current cast, or refuses it. The first tick of a cast is
    /// accepted with any sequence; after that the sequence byte must DIFFER from the last accepted one (an exact
    /// repeat is a replay and is dropped), at least <see cref="MinTickInterval"/> must have passed since it, and fewer
    /// than <see cref="MaxTicksPerCast"/> may have been accepted. A refused tick changes nothing. The run restarts with a fresh accepted 1509
    /// (<see cref="ResetTicks"/>), a new accepted 1508/1561 of the skill (<see cref="RecordPending"/>) and the end of
    /// the cast (its pending entry consumed). The caller has already required a pending cast (<see cref="TryGovern"/>),
    /// so a tick with no cast behind it never reaches here. Atomic per player.
    /// </summary>
    /// <remarks>
    /// Only an EXACT-DUPLICATE sequence (equal to the last accepted one) is refused — a replay guard — because
    /// "strictly increasing" (the backlog's CB-H3 rule) is not established: the 1549 sequence byte is not known
    /// to be a per-cast monotonic counter, so rejecting any non-increasing byte would silently drop legit
    /// multi-tick damage. <see cref="MinTickInterval"/> and <see cref="MaxTicksPerCast"/> are the real backstop.
    /// Channelled skills emit zero ticks (notes above), so none of this applies to them.
    /// </remarks>
    internal static bool TryAcceptTick(PlayerState player, uint skillId, byte sequence, DateTime nowUtc)
    {
        var flight = Store.GetValue(player, _ => new InFlight());
        lock (flight)
        {
            if (!flight.Ticks.TryGetValue(skillId, out var ledger))
            {
                if (flight.Ticks.Count >= MaxPendingPerPlayer)
                {
                    foreach (var stale in flight.Ticks.Where(t => nowUtc - t.Value.LastAtUtc > PendingGrace * 2)
                                 .Select(t => t.Key).ToList())
                        flight.Ticks.Remove(stale);
                    if (flight.Ticks.Count >= MaxPendingPerPlayer) return false;
                }
                flight.Ticks[skillId] = new TickLedger { LastSequence = sequence, Count = 1, LastAtUtc = nowUtc };
                return true;
            }
            if (sequence == ledger.LastSequence
                || ledger.Count >= MaxTicksPerCast
                || nowUtc - ledger.LastAtUtc < MinTickInterval)
                return false;
            ledger.LastSequence = sequence;
            ledger.Count++;
            ledger.LastAtUtc = nowUtc;
            return true;
        }
    }

    /// <summary>Starts <paramref name="skillId"/>'s tick run over (an accepted 1509 of it arrived).</summary>
    internal static void ResetTicks(PlayerState player, uint skillId)
    {
        if (!Store.TryGetValue(player, out var flight)) return;
        lock (flight) flight.Ticks.Remove(skillId);
    }
}
