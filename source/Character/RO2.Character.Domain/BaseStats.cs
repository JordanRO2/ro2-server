using RO2.Characters.Domain;
namespace RO2.Characters.Domain;

/// <summary>A character's class/level base stats before worn equipment: max HP/SP and the derived-stat map.</summary>
/// <param name="MaxHp">Base maximum HP.</param>
/// <param name="MaxSp">Base maximum SP.</param>
/// <param name="Derived">Derived stats (2498 stat id → value): AP, MA, Defense, Crit, Parry, Dodge, Hit.</param>
public readonly record struct BaseStats(uint MaxHp, ushort MaxSp, IReadOnlyDictionary<byte, int> Derived);
