using System.Globalization;

namespace RO2.Rankings.Domain;

/// <summary>Stored-key formats of the ranking collections; the exact strings are a frozen persisted surface.</summary>
public static class RankingKeys
{
    public static string SnapshotId(byte rankingType, DateTime periodStartUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{rankingType}:{periodStartUtc:yyyyMMddHHmm}");

    public static string ScoreId(byte rankingType, DateTime periodStartUtc, uint charId) =>
        string.Create(CultureInfo.InvariantCulture, $"{rankingType}:{periodStartUtc:yyyyMMddHHmm}:{charId}");
}
