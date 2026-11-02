using RO2.Monster.Domain;

namespace RO2.Monster.Application;

/// <summary>A player as the retaliation pass reads it: its actor handle and name, whether it is dead and its map.</summary>
public sealed record PlayerFacts(uint Handle, string Name, bool IsDead, uint MapId);

/// <summary>The monster facts the retaliation pass needs: its handle, its map and how it starts fights on its own.</summary>
public readonly record struct MonsterFacts(uint Oid, uint MapId, NpcAggressionMode Aggression);

/// <summary>Read side: resolves live state the decision depends on, re-read at the moment of each step.</summary>
public interface IMonsterReadView
{
    /// <summary>The live player behind a PC actor handle, or null for 0, an unknown session or a reused session slot.</summary>
    PlayerFacts? FindPlayer(uint pcHandle);

    /// <summary>The monster's current top-threat target handle (0 when none).</summary>
    uint AggroTargetOf(uint monsterOid);
}

/// <summary>Command side: the live-state mutations a monster decision orders.</summary>
public interface IMonsterCommands
{
    /// <summary>Adds threat on a monster's table (queues the 2630 feed); true when the aggro list changed shape.</summary>
    bool AddThreat(uint monsterOid, uint pcHandle, string? pcName, long amount);

    /// <summary>Drops the monster out of a fight: empties threat and target and restores full HP; true when anything changed.</summary>
    bool LeashReset(uint monsterOid);

    /// <summary>Re-points the monster at its highest-threat attacker still alive on its map; the new target handle, or 0.</summary>
    uint ReselectLiveTarget(uint monsterOid);
}

/// <summary>The two wire effects the pass orders between its state changes; the host sends them.</summary>
public interface IRetaliationNotifier
{
    /// <summary>The monster entered combat against the player it just acquired.</summary>
    Task EnterCombatAsync(uint monsterOid, uint pcHandle);

    /// <summary>The monster was reset and healed: show its restored HP.</summary>
    Task NotifyLeashHealedAsync(uint monsterOid);
}
