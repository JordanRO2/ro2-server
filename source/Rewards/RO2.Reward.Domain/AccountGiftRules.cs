namespace RO2.Rewards.Domain;

/// <summary>
/// The pure expiry rules of the account gift box. The record's u8 @38 is the days left: 7 on the day of filing and
/// one less each server day after, with the boundary at local midnight (every 2799 page with two or more records
/// agrees with <c>7 - (today - filing day)</c>, and none fits a boundary one hour off either way). It is never
/// below 0, so a gift is dropped the day after it reaches 0 (<see cref="IsLive"/>), and counts, lists and claims see
/// only live gifts.
/// </summary>
public static class AccountGiftRules
{
 /// <summary>Record @38 on the day a gift is filed (the largest value).</summary>
    public const int DaysKept = 7;

 /// <summary>Days left for a gift filed at <paramref name="receivedLocal"/>: 7 on that day, 0 seven days later.</summary>
    public static int DaysLeft(DateTime receivedLocal, DateTime nowLocal)
        => DaysKept - (nowLocal.Date - receivedLocal.Date).Days;

 /// <summary>Whether a gift is still in the box (<see cref="DaysLeft"/> not below 0).</summary>
    public static bool IsLive(AccountGift gift, DateTime nowLocal)
        => DaysLeft(ToLocal(gift.ReceivedUtc), nowLocal) >= 0;

 /// <summary>The live gifts of <paramref name="gifts"/>, newest first.</summary>
    public static List<AccountGift> NewestFirst(IEnumerable<AccountGift> gifts, DateTime nowLocal)
        => gifts.Where(g => IsLive(g, nowLocal)).OrderByDescending(g => g.ReceivedUtc).ToList();

 /// <summary>
 /// Page window: page P lists the live gifts, newest first, starting at index <c>5 * (P - 1)</c> (floored at 0)
 /// and holding up to 7 entries. Consecutive pages share two gifts.
 /// </summary>
    public const int PageStride = 5, PageRows = 7;

 /// <summary>
 /// The first index and count of the gifts that page <paramref name="page"/> lists out of <paramref name="total"/>.
 /// </summary>
    public static (int Start, int Count) PageWindow(int total, ushort page)
    {
        int from = PageStride * (page - 1);
        int to = Math.Min(total, from + PageRows);
        from = Math.Max(0, from);
        return (from, Math.Max(0, to - from));
    }

 /// <summary>Creates a gift with the given id (32 hex digits) filed at <paramref name="nowUtc"/>.</summary>
    public static AccountGift NewGift(string guid, uint itemId, ushort count, byte source, DateTime nowUtc) => new()
    {
        Guid = guid,
        ItemId = itemId,
        Count = count,
        ReceivedUtc = nowUtc,
        Source = source,
    };

 /// <summary>A stored UTC instant on the server's local clock, the one the wire carries.</summary>
    public static DateTime ToLocal(DateTime storedUtc) => DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc).ToLocalTime();
}
