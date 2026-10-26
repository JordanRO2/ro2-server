using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;
using RO2.Combats.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host facade over one <see cref="CastLedger"/> per PlayerState: the cast state (groups in flight, pending casts,
/// 1549 tick ledgers, open 1506 cast bars) for the 1508/1509/1549/1506/1507/1561 path. The ledger logic and its
/// locking live in <see cref="CastLedger"/> (Combat.Domain); this maps player → ledger through a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> (so it leaves with the player, needs no DI, and a relog starts
/// empty) and supplies the catalog-fed cast durations (<see cref="SkillCasterBusyWindow.CastDuration"/>).
/// </summary>
public static class SkillCastLandings
{
    /// <summary>Slack added to a skill's cast duration before an unconsumed pending cast / open cast bar lapses (7 s).</summary>
    public static readonly TimeSpan PendingGrace = CastLedger.PendingGrace;

    /// <summary>Shortest gap accepted between two 1549 ticks of one cast (100 ms).</summary>
    public static readonly TimeSpan MinTickInterval = CastLedger.MinTickInterval;

    /// <summary>Most ticks accepted for one cast (64).</summary>
    public const int MaxTicksPerCast = CastLedger.MaxTicksPerCast;

    /// <summary>Most pending casts / cast bars kept per player (32).</summary>
    public const int MaxPendingPerPlayer = CastLedger.MaxPendingPerPlayer;

    private static readonly ConditionalWeakTable<PlayerState, CastLedger> Store = new();

    private static CastLedger LedgerOf(PlayerState player) => Store.GetValue(player, _ => new CastLedger());

    /// <summary>A 1508 of <paramref name="skillGroup"/> was accepted: its cast is in flight until its 1509.</summary>
    public static void BeginCast(PlayerState player, uint skillGroup) => LedgerOf(player).BeginCast(skillGroup);

    /// <summary>The caster's 1509 for <paramref name="skillId"/> arrived: its group's cast has landed.</summary>
    public static void MarkLanded(PlayerState player, uint skillId)
    {
        if (Store.TryGetValue(player, out var l)) l.MarkLanded(skillId);
    }

    /// <summary>True when a 1509 of <paramref name="skillGroup"/> has arrived since its last accepted 1508, and when it was never in flight.</summary>
    public static bool HasLanded(PlayerState player, uint skillGroup)
        => !Store.TryGetValue(player, out var l) || l.HasLanded(skillGroup);

    /// <summary>
    /// Records an accepted 1508/1561 as a pending cast. It expires at <paramref name="nowUtc"/> plus
    /// <paramref name="lifetime"/>, or by default the skill's cast duration (<see cref="SkillCasterBusyWindow.CastDuration"/>)
    /// plus <see cref="PendingGrace"/>.
    /// </summary>
    internal static void RecordPending(PlayerState player, uint skillId, uint targetHandle, DateTime nowUtc,
        TimeSpan? lifetime = null)
    {
        var life = lifetime ?? SkillCasterBusyWindow.CastDuration(skillId) + CastLedger.PendingGrace;
        LedgerOf(player).RecordPending(skillId, targetHandle, nowUtc, life);
    }

    /// <summary>Consumes the oldest unexpired pending cast of exactly <paramref name="skillId"/> (and <paramref name="targetHandle"/> when given).</summary>
    internal static bool TryConsume(PlayerState player, uint skillId, DateTime nowUtc, out PendingCast cast,
        uint? targetHandle = null)
    {
        cast = default;
        return Store.TryGetValue(player, out var l) && l.TryConsume(skillId, nowUtc, out cast, targetHandle);
    }

    /// <summary>As <see cref="TryConsume"/>, matching any level of <paramref name="skillGroup"/>.</summary>
    internal static bool TryConsumeGroup(PlayerState player, uint skillGroup, DateTime nowUtc, out PendingCast cast)
    {
        cast = default;
        return Store.TryGetValue(player, out var l) && l.TryConsumeGroup(skillGroup, nowUtc, out cast);
    }

    /// <summary>The 1509/1549 damage gate: true with the governing cast; <paramref name="consume"/> spends a non-channelled 1509's single cast.</summary>
    internal static bool TryGovern(PlayerState player, uint skillId, DateTime nowUtc, bool consume, out PendingCast cast)
    {
        cast = default;
        return Store.TryGetValue(player, out var l) && l.TryGovern(skillId, nowUtc, consume, out cast);
    }

    /// <summary>Drops the player's expired pending casts; returns how many it removed.</summary>
    internal static int PruneExpired(PlayerState player, DateTime nowUtc)
        => Store.TryGetValue(player, out var l) ? l.PruneExpired(nowUtc) : 0;

    /// <summary>How many unexpired pending casts the player holds.</summary>
    internal static int PendingCount(PlayerState player, DateTime nowUtc)
        => Store.TryGetValue(player, out var l) ? l.PendingCount(nowUtc) : 0;

    /// <summary>Records an accepted 1506 casting start: its bar is open until a 1507, its 1508/1509, or its cast duration + grace lapses.</summary>
    internal static void BeginCasting(PlayerState player, uint skillId, DateTime nowUtc)
    {
        var expiresAt = nowUtc + SkillCasterBusyWindow.CastDuration(skillId) + CastLedger.PendingGrace;
        LedgerOf(player).BeginCasting(skillId, nowUtc, expiresAt);
    }

    /// <summary>True when <paramref name="skillId"/> has an open cast bar or an unexpired pending cast of that exact id.</summary>
    internal static bool IsCasting(PlayerState player, uint skillId, DateTime nowUtc)
        => Store.TryGetValue(player, out var l) && l.IsCasting(skillId, nowUtc);

    /// <summary>Closes <paramref name="skillId"/>'s cast bar (a 1507 cancelled it); its pending cast is left to its own 1509/expiry.</summary>
    internal static void EndCasting(PlayerState player, uint skillId)
    {
        if (Store.TryGetValue(player, out var l)) l.EndCasting(skillId);
    }

    /// <summary>Admits one 1549 tick of <paramref name="skillId"/>'s current cast, or refuses it (see <see cref="CastLedger.TryAcceptTick"/>).</summary>
    internal static bool TryAcceptTick(PlayerState player, uint skillId, byte sequence, DateTime nowUtc)
        => LedgerOf(player).TryAcceptTick(skillId, sequence, nowUtc);

    /// <summary>Starts <paramref name="skillId"/>'s tick run over (an accepted 1509 of it arrived).</summary>
    internal static void ResetTicks(PlayerState player, uint skillId)
    {
        if (Store.TryGetValue(player, out var l)) l.ResetTicks(skillId);
    }
}
