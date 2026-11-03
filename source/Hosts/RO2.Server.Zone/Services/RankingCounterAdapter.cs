using Microsoft.Extensions.Logging;
using RO2.Companions.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IRankingCounter"/> with <see cref="RankingService.CountAsync(IServiceProvider, uint, string, byte, ILogger)"/>; the log name comes from the online player.</summary>
internal sealed class RankingCounterAdapter(IServiceProvider services, PlayerStateManager players, ILogger logger) : IRankingCounter
{
    public Task CountAsync(uint characterId, byte rankingType)
        => RankingService.CountAsync(services, characterId, players.GetPlayerByCharacterId(characterId)?.Name ?? string.Empty, rankingType, logger);
}
