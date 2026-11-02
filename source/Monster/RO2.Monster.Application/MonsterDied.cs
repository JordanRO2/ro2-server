namespace RO2.Monster.Application;

/// <summary>
/// The fact "this monster was just killed", built once from the <c>died</c> flag <c>NpcStateManager.ApplyDamage</c>
/// returns (kill detected under HpLock) and consumed by every damage path's post-death tail. It carries only the
/// identity the shared kill grant needs; each path still decides and sends its own death frames in its own order.
/// </summary>
/// <param name="Handle">The monster's actor handle at the moment of the kill (the corpse key).</param>
/// <param name="NpcId">The monster's catalog id (drops, EXP and quest credit key off it).</param>
/// <param name="KillerHandle">The actor handle of the attacker whose hit or DoT tick landed the killing blow.</param>
public readonly record struct MonsterDied(uint Handle, uint NpcId, uint KillerHandle);
