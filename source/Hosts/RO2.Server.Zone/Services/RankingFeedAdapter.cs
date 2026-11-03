using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Rankings.Application;
using RO2.Rankings.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Backs <see cref="IRankingFeed"/>: resolves the repository and store nullable (tests without them stay a no-op),
/// gates, starts the roll clock, then runs the <see cref="CountRankingUseCase"/> add and logs storage errors.
/// </summary>
internal sealed class RankingFeedAdapter(IServiceProvider services, CountRankingUseCase counts, ILogger logger) : IRankingFeed
{
    public async Task CountAsync(uint characterId, string name, byte rankingType, int amount, CancellationToken ct = default)
    {
        var repo = services.GetService<IRankingRepository>();
        var store = services.GetService<GameDataStore>();
        if (repo is null || store is null || amount <= 0 || RankingFeeds.FeedOf(rankingType) != RankingFeed.Counter)
            return;
        RankingService.EnsureClockRunning(services);
        try
        {
            await counts.AddAsync(repo, store, characterId, rankingType, amount, DateTime.UtcNow, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: ranking type {Type} counter could not be stored", name, rankingType);
        }
    }

    public Task CountCardLandingAsync(uint characterId, string name, uint itemId, int landedCount,
        IReadOnlyList<int> stackCounts, CancellationToken ct = default)
    {
        var store = services.GetService<GameDataStore>();
        int n = store is null ? 0 : counts.CardRows(store, itemId, landedCount, stackCounts);
        return n > 0 ? CountAsync(characterId, name, RankingSchedule.TypeCardCollector, n, ct) : Task.CompletedTask;
    }
}
