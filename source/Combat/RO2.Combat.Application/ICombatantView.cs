namespace RO2.Combats.Application;

/// <summary>The combat facts of one actor the cast-path flows read, resolved by the host from its live NPC state.</summary>
public readonly record struct Combatant(uint EntityOid, bool IsDead, int Camp);

/// <summary>
/// Resolves an actor handle to its live combat facts, behind a port so Combat.Application does not reach the host
/// NpcStateManager / NpcRuntimeState directly. <see cref="Find"/> returns null when the handle is not a live NPC
/// (it is a PC, a corpse, or unknown).
/// </summary>
public interface ICombatantView
{
    /// <summary>The live combatant at <paramref name="handle"/>, or null when it is not a live NPC.</summary>
    Combatant? Find(uint handle);
}
