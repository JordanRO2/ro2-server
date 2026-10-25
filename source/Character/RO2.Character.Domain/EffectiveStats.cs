using RO2.Characters.Domain;
namespace RO2.Characters.Domain;

/// <summary>A character's gear-adjusted combat inputs: primaries plus the derived stats the 2498 sheet carries.</summary>
/// <param name="Str">Effective STR.</param>
/// <param name="Agi">Effective AGI.</param>
/// <param name="Int">Effective INT.</param>
/// <param name="Wis">Effective WIS.</param>
/// <param name="Vit">Effective VIT.</param>
/// <param name="Luk">Effective LUK.</param>
/// <param name="MaxHp">Maximum HP.</param>
/// <param name="MaxSp">Maximum SP.</param>
/// <param name="Defense">Defense (naked VitDefense plus worn flat armour).</param>
/// <param name="Vigor">Vigor.</param>
/// <param name="Crit">Critical.</param>
/// <param name="Parry">Parry / guard.</param>
/// <param name="Pen">Penetration.</param>
public readonly record struct EffectiveStats(
    int Str, int Agi, int Int, int Wis, int Vit, int Luk,
    uint MaxHp, ushort MaxSp, int Defense, int Vigor, int Crit, int Parry, int Pen);
