using RO2.Monster.Domain;

namespace RO2.Monster.Application;

/// <summary>What the host does with an aggroed monster this chase pass.</summary>
public enum ChaseAction
{
    /// <summary>Nothing to do: target gone or non-resolvable, or already within reach.</summary>
    None,
    /// <summary>The monster is past its leash: the host runs it home in one move, then resets it.</summary>
    LeashHome,
    /// <summary>The monster closes the gap: the host moves it to the verdict's point.</summary>
    Step,
}

/// <summary>The chase decision: the action and, for <see cref="ChaseAction.Step"/>, the point to move to.</summary>
public readonly record struct ChaseVerdict(ChaseAction Action, float X, float Y);

/// <summary>The monster facts the chase decision reads: where it is, its home and leash, its reach and run speed.</summary>
public readonly record struct ChaseMonster(uint MapId, float PosX, float PosY, float HomeX, float HomeY,
    float LeashBound, float AttackReach, float FallbackReach, float Speed);

/// <summary>The monster's target as the chase reads it.</summary>
public readonly record struct ChaseTarget(float X, float Y, bool IsDead, uint MapId);

/// <summary>
/// The per-tick chase decision of an aggroed monster: leash FIRST (dragged past its bound it gives up), then, if its
/// target is still live on its map, the step that closes the gap to its attack reach. Pure: the host keeps the tick
/// loop and cadence, resolves the speed and the target, and applies the verdict (2318 move, position and cell writes,
/// leash reset and heal) in order.
/// </summary>
public static class ChasePass
{
    /// <summary>
    /// Decides one monster. <paramref name="resolveTarget"/> is called only when the monster is not past its leash,
    /// matching the host's lazy lookup.
    /// </summary>
    public static ChaseVerdict Decide<TState>(in ChaseMonster m, double dt, TState state, Func<TState, ChaseTarget?> resolveTarget)
    {
        if (ChaseGeometry.IsPastLeash(m.PosX, m.PosY, m.HomeX, m.HomeY, m.LeashBound))
            return new ChaseVerdict(ChaseAction.LeashHome, m.HomeX, m.HomeY);

        if (resolveTarget(state) is not { } target || target.IsDead || target.MapId != m.MapId)
            return new ChaseVerdict(ChaseAction.None, 0f, 0f);   // target gone: the combat service re-selects or resets it

        return ChaseGeometry.DecideStep(m.PosX, m.PosY, target.X, target.Y, m.AttackReach, m.FallbackReach, m.Speed, dt)
            is var (x, y)
            ? new ChaseVerdict(ChaseAction.Step, x, y)
            : new ChaseVerdict(ChaseAction.None, 0f, 0f);
    }
}
