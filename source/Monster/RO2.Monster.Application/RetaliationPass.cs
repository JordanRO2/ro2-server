using RO2.Monster.Domain;

namespace RO2.Monster.Application;

/// <summary>What the host does with a monster after the pre-swing steps.</summary>
public enum PreSwingAction
{
    /// <summary>Nothing to do this tick; keep the swing timer.</summary>
    Skip,
    /// <summary>The monster left the fight: drop its swing timer.</summary>
    Reset,
    /// <summary>The monster acts: the host selects its victim and swings.</summary>
    Act,
}

/// <summary>The outcome of the pre-swing steps: the action, the live target handle (0 for none) and whether the
/// monster's target changed (the host pushes the target change to watchers).</summary>
public readonly record struct PreSwingVerdict(PreSwingAction Action, uint TargetHandle, bool Retargeted);

/// <summary>
/// The pre-swing part of a monster's per-tick combat decision: acquire a target if it has none, replace a target that
/// died or left the map, reset when nobody is left. The state changes and the two wire effects go through the ports, in
/// the order the host's retaliation pass always applied them; the swing itself (victim reach, timer, damage, sends)
/// stays in the host.
/// </summary>
public sealed class RetaliationPass(IMonsterReadView view, IMonsterCommands commands, IRetaliationNotifier notifier)
{
    /// <summary>Threat seated when a monster acquires a player by proximity: enough to make that player its target.</summary>
    public const long AcquireThreat = 1;

    /// <summary>
    /// Runs the steps for one hostile, live monster. <paramref name="hasIterating"/> is whether a player is engaged with
    /// it; <paramref name="scanNearest"/> is the host's sight scan, called only when the monster may acquire.
    /// </summary>
    public async Task<PreSwingVerdict> RunAsync<TState>(MonsterFacts monster, bool hasIterating, TState state,
        Func<TState, PlayerFacts?> scanNearest)
    {
        bool retargeted = false;

        // (a) ACQUISITION: an aggressive, not-yet-fighting monster takes the nearest player in its sight.
        if (view.AggroTargetOf(monster.Oid) == 0 && !hasIterating)
        {
            if (monster.Aggression == NpcAggressionMode.None)
                return new PreSwingVerdict(PreSwingAction.Skip, 0, false);
            var acquired = scanNearest(state);
            if (acquired is null)
                return new PreSwingVerdict(PreSwingAction.Skip, 0, false);
            commands.AddThreat(monster.Oid, acquired.Handle, acquired.Name, AcquireThreat);
            await notifier.EnterCombatAsync(monster.Oid, acquired.Handle);
            retargeted = true;
        }

        // (b) TARGET VALIDATION: a top-threat target that died or left the map gives way to the next-highest attacker.
        var target = view.FindPlayer(view.AggroTargetOf(monster.Oid));
        if (view.AggroTargetOf(monster.Oid) != 0 && (target is null || target.IsDead || target.MapId != monster.MapId))
        {
            target = view.FindPlayer(commands.ReselectLiveTarget(monster.Oid));
            retargeted = true;
        }

        // (c) RESET: no live target and nobody engaged.
        if (target is null && !hasIterating)
        {
            if (commands.LeashReset(monster.Oid))
            {
                retargeted = true;
                await notifier.NotifyLeashHealedAsync(monster.Oid);
            }
            return new PreSwingVerdict(PreSwingAction.Reset, 0, retargeted);
        }

        return new PreSwingVerdict(PreSwingAction.Act, target?.Handle ?? 0, retargeted);
    }
}
