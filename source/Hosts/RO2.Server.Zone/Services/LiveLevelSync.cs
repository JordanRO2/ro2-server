using RO2.Server.Zone.Models;
using RO2.Characters.Domain;
using RO2.Worlds.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Brings the character row's level up to the live <see cref="PlayerState.Level"/>. Shared by the services and the handlers
/// that build the self 2315 or 2498 from the row, so none has to call another handler for it.
/// </summary>
internal static class LiveLevelSync
{
 /// <summary>
 /// Sets the row's level to the live level when the live level is known and differs. A level-up moves only
 /// PlayerState.Level; the world-entry snapshot keeps the login level.
 /// </summary>
    internal static void Apply(PlayerState player, Character? character)
    {
        if (character is not null && MapLoadUseCase.LiveLevelOverride(character.Level, player.Level) is { } level)
            character.Level = level;
    }
}
