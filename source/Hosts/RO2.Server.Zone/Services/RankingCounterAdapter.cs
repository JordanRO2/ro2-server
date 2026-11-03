using Microsoft.Extensions.Logging;
using RO2.Companions.Application;
using RO2.Rankings.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IRankingCounter"/> with <see cref="IRankingFeed.CountAsync"/>; the log name comes from the online player.</summary>
internal sealed class RankingCounterAdapter(IRankingFeed feed, PlayerStateManager players) : IRankingCounter
{
    public Task CountAsync(uint characterId, byte rankingType)
        => feed.CountAsync(characterId, players.GetPlayerByCharacterId(characterId)?.Name ?? string.Empty, rankingType, 1);
}
