using RO2.GameData;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host convenience over the pure <see cref="NameRules"/>: validates a character name against the loaded
/// name_filter table (<see cref="GameDataStore.Loaded"/>). Used by character creation and the name-change scroll;
/// the name check calls <see cref="NameRules.Check"/> with the store it resolves, so all three apply the same rule.
/// </summary>
public static class NameValidator
{
    /// <summary>(valid, reason) for a name checked against the loaded name_filter; reason is null when valid.</summary>
    public static (bool valid, string? reason) ValidateFormat(string? name)
    {
        var (refusal, reason) = NameRules.Check(name, GameDataStore.Loaded?.NameFilter);
        return (refusal == NameRefusal.None, reason);
    }
}
