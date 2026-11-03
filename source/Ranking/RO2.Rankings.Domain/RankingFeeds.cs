namespace RO2.Rankings.Domain;

/// <summary>How a ranking type is fed on this server.</summary>
public enum RankingFeed { None, Counter }

/// <summary>Which ranking types have a gameplay feed (the other types publish empty boards).</summary>
public static class RankingFeeds
{
    /// <summary>What feeds <paramref name="rankingType"/> on this server.</summary>
    public static RankingFeed FeedOf(byte rankingType) => rankingType switch
    {
        RankingSchedule.TypeKafraTransport or RankingSchedule.TypeMonsterHunt or RankingSchedule.TypeCardCollector
            => RankingFeed.Counter,
        _ => RankingFeed.None,
    };

    /// <summary>The types with a feed, the only ones the roll publishes.</summary>
    public static readonly byte[] FedTypes =
        { RankingSchedule.TypeKafraTransport, RankingSchedule.TypeMonsterHunt, RankingSchedule.TypeCardCollector };
}
