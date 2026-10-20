namespace RO2.Domain.Entities;

/// <summary>One ranked character in a published board.</summary>
public sealed class RankingSnapshotRow
{
    public uint CharId { get; set; }
    public uint Rank { get; set; }
    public uint PreviousRank { get; set; }
    public byte ClassId { get; set; }
    public byte Profession { get; set; }
    public long Score { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Guild { get; set; } = string.Empty;
}

/// <summary>
/// A board as published at a reset instant (collection <c>ranking_snapshots</c>): every ranked character
/// of one type, rank 1..n, frozen for the period that starts at <see cref="PeriodStartUtc"/>.
/// </summary>
public sealed class RankingSnapshotDocument
{
 /// <summary>
 /// <c>"{type}:{periodStart:yyyyMMddHHmm}"</c>. One document per type and period; the first process to
 /// roll a period owns it.
 /// </summary>
    public string Id { get; set; } = string.Empty;
    public byte Type { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime RolledUtc { get; set; }
    public List<RankingSnapshotRow> Rows { get; set; } = new();
}
