namespace RO2.Economy.Domain;

/// <summary>
/// The pure box rules of an account's cash-mall storage: which entries of a box are still live and in what order
/// (newest first — ids descending within a filing day, every retail 2639/2640 with two or more entries), and the
/// two 2638 counts. The persistence is ICashMallRepository; the wire builders and the OLE-date conversion stay in
/// the Zone host.
/// </summary>
public static class CashMallRules
{
 /// <summary>1471 box 1 / 2639: what the account bought for itself.</summary>
    public const byte BoxPurchased = 1;

 /// <summary>1471 box 2 / 2640: gifts another account (or this one) bought for it.</summary>
    public const byte BoxGift = 2;

 /// <summary>Entries of <paramref name="box"/> that still hold units, newest first (ids descending within a
 /// filing day). Null is treated as an empty box.</summary>
    public static IReadOnlyList<CashMallBoxEntry> LiveEntries(IEnumerable<CashMallBoxEntry>? entries, byte box)
        => entries is null
            ? new List<CashMallBoxEntry>()
            : entries.Where(e => e.Box == box && e.Count > 0)
                .OrderByDescending(e => e.FiledUtc).ThenByDescending(e => e.EntryId).ToList();

 /// <summary>The two 2638 counts: entries (not units) waiting in box 1 and in box 2.</summary>
    public static (int Purchased, int Gifts) BoxCounts(IEnumerable<CashMallBoxEntry>? entries)
        => (LiveEntries(entries, BoxPurchased).Count, LiveEntries(entries, BoxGift).Count);
}
