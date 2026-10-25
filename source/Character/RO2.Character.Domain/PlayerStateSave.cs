namespace RO2.Characters.Domain;

/// <summary>One player's frequently-changing persistent state, for the bulk autosave.</summary>
/// <param name="CharacterId">The character id.</param>
/// <param name="MapId">Current map id.</param>
/// <param name="X">World X position.</param>
/// <param name="Y">World Y position.</param>
/// <param name="Z">World Z position.</param>
/// <param name="Hp">Current HP.</param>
/// <param name="MaxHp">Maximum HP.</param>
/// <param name="Mp">Current MP.</param>
/// <param name="MaxMp">Maximum MP.</param>
/// <param name="Level">Base level.</param>
/// <param name="Exp">Base EXP.</param>
/// <param name="Gold">Carried zeny.</param>
/// <param name="BankGold">Banked zeny.</param>
/// <param name="Job">Base combat class.</param>
/// <param name="BindIndex">Bound save-point index.</param>
/// <param name="FacingYaw">Facing yaw in radians.</param>
public readonly record struct PlayerStateSave(
    uint CharacterId, uint MapId, float X, float Y, float Z,
    int Hp, int MaxHp, int Mp, int MaxMp, byte Level, ulong Exp, long Gold, long BankGold, byte Job, ushort BindIndex,
    float FacingYaw);
