using RO2.GameData;

namespace RO2.Monster.Domain;

/// <summary>
/// The identity and catalog values of a spawned NPC, fixed when its spawn is tracked and never changed by play.
/// Immutable, so a corpse snapshot shares the live NPC's definition by reference.
/// </summary>
public sealed record NpcDefinition
{
    /// <summary>NPCInfo asset index.</summary>
    public uint NpcId { get; init; }
    /// <summary>Map the spawn lives on.</summary>
    public uint MapId { get; init; }
    /// <summary>Max HP.</summary>
    public int MaxHp { get; init; }

    /// <summary>Max SP, which is also its current SP (NPCs spend no SP). 0 means it was never resolved.</summary>
    public int MaxSp { get; init; }
    /// <summary>Attack.</summary>
    public int Atk { get; init; }
    /// <summary>Defense.</summary>
    public int Def { get; init; }
    /// <summary>Exp granted on kill.</summary>
    public int ExpReward { get; init; }
    /// <summary>Camp (&lt;= 1 is friendly).</summary>
    public int Camp { get; init; }

    /// <summary>Whether the corpse can be gathered (<c>NPCInfo.AfterDeath_FuncObject_ID != 0</c>); record+66 on the wire.</summary>
    public bool CorpseGatherable { get; init; }

    /// <summary>NPC level. Goes on the wire at 2506 record offset +8.</summary>
    public int Level { get; init; }

    /// <summary>Horizontal reach this NPC lands a hit from (NPCInfo.Attack_Range, else the melee default); 0 = never resolved.</summary>
    public float AttackReach { get; init; }

    /// <summary>The spawn row this NPC came from.</summary>
    public SpawnEntry Spawn { get; init; } = null!;
}
