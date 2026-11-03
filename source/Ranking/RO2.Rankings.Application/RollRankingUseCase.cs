using RO2.GameData.Tables;
using RO2.Rankings.Domain;

namespace RO2.Rankings.Application;

/// <summary>
/// The roll decision: the board of the period starting at <c>period</c>, from the counters of the period before it,
/// ranked against the board it replaces for the previous ranks. The clock, the cache, the roll gate and the
/// publishing stay with the Zone host; the host passes the time in.
/// </summary>
public sealed class RollRankingUseCase(IRankingRepository repo, ICharacterRankingLookup characters, IGuildNameLookup guilds)
{
    private static readonly IReadOnlyDictionary<uint, CharacterRankingInfo> None = new Dictionary<uint, CharacterRankingInfo>();

 /// <summary>Builds the snapshot for <paramref name="period"/>, stamped <paramref name="now"/>.</summary>
    public async Task<RankingSnapshotDocument> DecideAsync(byte rankingType, RankingReset reset, DateTime period,
        DateTime now, RankingSnapshotDocument? replaced, CancellationToken ct)
    {
        var ended = RankingSchedule.PreviousPeriodStart(reset, period);
        var docs = await repo.GetPeriodScoresAsync(rankingType, ended, ct);
        var scores = docs.Select(d => (d.CharId, d.Score, d.ReachedUtc)).ToList();

        var ids = scores.Where(s => s.Score > 0).Select(s => s.CharId).Distinct().ToList();
        var chars = ids.Count == 0 ? None : await characters.LookupAsync(ids, ct);
        // The non-deleted keys: an empty set never initializes the guild source.
        var guildNames = await guilds.GuildNamesAsync(chars.Keys.ToList(), ct);

        var standings = new List<Standing>(scores.Count);
        foreach (var (charId, score, reached) in scores)
        {
            if (!chars.TryGetValue(charId, out var c)) continue;
            standings.Add(new Standing(charId, score, reached, c.Name, c.ClassId, c.Profession,
                guildNames.GetValueOrDefault(charId, string.Empty)));
        }

        return new RankingSnapshotDocument
        {
            Id = RankingKeys.SnapshotId(rankingType, period),
            Type = rankingType,
            PeriodStartUtc = period,
            RolledUtc = now,
            Rows = RankingBoard.RankRows(standings, replaced?.Rows),
        };
    }
}
