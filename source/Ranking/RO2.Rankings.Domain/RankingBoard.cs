namespace RO2.Rankings.Domain;

/// <summary>The pure board rules: ranking a roll's standings and the class/profession filter.</summary>
public static class RankingBoard
{
    public static bool Matches(RankingSnapshotRow row, byte filter)
    {
        var (selection, filterType) = RankingSchedule.DecodeFilter(filter);
        if (selection == 0) return true;
        return filterType switch
        {
            RankingSchedule.FilterByClass => row.ClassId == selection,
            RankingSchedule.FilterByProfession => row.Profession == selection,
            _ => true,
        };
    }

 /// <summary>
 /// Ranks <paramref name="standings"/> into a board: scores above 0 only, highest first, then the earliest reached, then
 /// the lower character id. Ranks run 1..n with no gaps. The previous rank is the character's rank in
 /// <paramref name="previous"/>, or 0 when it was not on it.
 /// </summary>
    public static List<RankingSnapshotRow> RankRows(IEnumerable<Standing> standings,
        IReadOnlyList<RankingSnapshotRow>? previous)
    {
        var before = new Dictionary<uint, uint>();
        if (previous != null)
            foreach (var p in previous)
                before.TryAdd(p.CharId, p.Rank);

        var rows = new List<RankingSnapshotRow>();
        uint rank = 0;
        foreach (var s in standings.Where(s => s.Score > 0)
                     .OrderByDescending(s => s.Score).ThenBy(s => s.ReachedUtc).ThenBy(s => s.CharId))
        {
            rows.Add(new RankingSnapshotRow
            {
                CharId = s.CharId,
                Rank = ++rank,
                PreviousRank = before.GetValueOrDefault(s.CharId),
                ClassId = s.ClassId,
                Profession = s.Profession,
                Score = s.Score,
                Name = s.Name,
                Guild = s.Guild,
            });
        }
        return rows;
    }
}
