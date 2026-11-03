namespace RO2.Colosseum.Domain;

/// <summary>One scoreboard row: a player name and its single-byte score.</summary>
public readonly record struct ColosseumStanding(string Name, byte Score);

/// <summary>Pure Colosseum scoreboard rules: row order, score clamp and the ranking byte.</summary>
public static class ColosseumScoreboard
{
    /// <summary>Highest score a scoreboard row carries: each row holds a single byte.</summary>
    public const int ScoreMax = byte.MaxValue;

    /// <summary>
    /// Orders the rows: the registrants first (in the given join order, deduplicated), then the other ranked players by score from highest to lowest. Scores clamp to 0..<see cref="ScoreMax"/>.
    /// </summary>
    public static IReadOnlyList<ColosseumStanding> Order(IReadOnlyList<string> registrantNamesInJoinOrder, IReadOnlyDictionary<string, int> scores)
    {
        var rows = new List<ColosseumStanding>();
        var seen = new HashSet<string>();
        foreach (var name in registrantNamesInJoinOrder)
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            scores.TryGetValue(name, out int sc);
            rows.Add(new ColosseumStanding(name, (byte)Math.Clamp(sc, 0, ScoreMax)));
        }
        foreach (var kv in scores.OrderByDescending(k => k.Value))
        {
            if (!seen.Add(kv.Key)) continue;
            rows.Add(new ColosseumStanding(kv.Key, (byte)Math.Clamp(kv.Value, 0, ScoreMax)));
        }
        return rows;
    }

    /// <summary>The ranking value sent with the ranking update: the number of ranked players, low byte.</summary>
    public static byte RankingByte(int scoreCount) => (byte)(scoreCount & byte.MaxValue);
}
