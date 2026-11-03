namespace RO2.Rewards.Domain;

/// <summary>
/// The 28-day attendance calendar of an unbroken cycle: for each day, the entry flag, the reward count and the reward item id.
/// The rows are retail-pinned constants (the 3027 popup hex tests are authoritative).
/// </summary>
/// <remarks>
/// <para>
/// The calendar does not rotate by month. Only the milestone days (7, 14, 21 and 28) differ between accounts: each shows
/// either a pouch reward or a seal reward, decided by the account's streak (see <see cref="EntryFor"/>).
/// </para>
/// </remarks>
public static class AttendanceCalendar
{
 /// <summary>Attendance entry flags: a plain reward day, with no bit of its own set.</summary>
    public const byte FlagPlain = 0x00;

 /// <summary>Attendance entry flag 0x10: the diamond-pouch reward of a milestone day on an unbroken streak.</summary>
    public const byte FlagPouch = 0x10;

 /// <summary>
 /// Item ids (ItemInfo, Item_Type 129) of the diamond pouch rewarded on milestone days 7, 14, 21 and 28 of an unbroken streak.
 /// </summary>
    private const uint DiamondPouchDay7 = 50001113, DiamondPouchDay14 = 50001114, DiamondPouchDay21 = 50001115, DiamondPouchDay28 = 50001116;

 /// <summary>
 /// Item ids (ItemInfo, Item_Type 127) of the seal rewarded on milestone days 7, 14, 21 and 28 once the streak has broken.
 /// </summary>
    private const uint SealDay7 = 22402012, SealDay14 = 22402013, SealDay21 = 22402014, SealDay28 = 22402015;

 /// <summary>Reward count of the seal milestone entry.</summary>
    private const byte SealCount = 1;

 /// <summary>The milestone days of the calendar: every seventh day (7, 14, 21 and 28).</summary>
    private const int MilestoneEvery = 7;

 /// <summary>Length of the calendar: the cycle the attended-day count wraps at.</summary>
    public const int Days = 28;

    private static readonly (byte Flag, byte Count, uint ItemId)[] Rows =
    {
        (FlagPlain, 1, 60000165), (FlagPlain, 1, 60000474), (FlagPlain, 1, 60000486), (FlagPlain, 1, 60000076),
        (FlagPlain, 1, 60000277), (FlagPlain, 1, 50001072), (FlagPouch, 1, DiamondPouchDay7), (FlagPlain, 1, 60000086),
        (FlagPlain, 2, 60000474), (FlagPlain, 2, 60000486), (FlagPlain, 1, 60000076), (FlagPlain, 1, 60000277),
        (FlagPlain, 1, 50001072), (FlagPouch, 1, DiamondPouchDay14), (FlagPlain, 1, 60000058), (FlagPlain, 3, 60000474),
        (FlagPlain, 3, 60000486), (FlagPlain, 1, 60000076), (FlagPlain, 1, 60000277), (FlagPlain, 1, 50001072),
        (FlagPouch, 1, DiamondPouchDay21), (FlagPlain, 1, 60000065), (FlagPlain, 4, 60000474), (FlagPlain, 4, 60000486),
        (FlagPlain, 1, 60000076), (FlagPlain, 1, 60000277), (FlagPlain, 1, 50001072), (FlagPouch, 1, DiamondPouchDay28),
    };

 /// <summary>
 /// The milestone entry of a broken streak, for milestone k (day 7k) at index k-1: a seal reward with count 1 and flag 0.
 /// </summary>
    private static readonly uint[] SealMilestones = { SealDay7, SealDay14, SealDay21, SealDay28 };

 /// <summary>
 /// The calendar entry of day <paramref name="day"/> (1 to 28) for an account whose streak broke after
 /// <paramref name="streakBrokenAfter"/> attended days (0 when the cycle is unbroken).
 /// </summary>
 /// <remarks>
 /// <para>
 /// A milestone is a pouch reward while the cycle's attendance has been unbroken since day 1. Once a day is missed, the
 /// milestones not yet reached become seals, and the milestones already reached keep their pouch. A cycle that wraps from
 /// 28 back to 1 starts unbroken again.
 /// </para>
 /// </remarks>
    public static (byte Flag, byte Count, uint ItemId) EntryFor(int day, int streakBrokenAfter)
    {
        var entry = Rows[day - 1];
        if (day % MilestoneEvery != 0 || streakBrokenAfter <= 0 || day <= streakBrokenAfter)
            return entry;
        return (FlagPlain, SealCount, SealMilestones[day / MilestoneEvery - 1]);
    }

 /// <summary>The reward item and count of an attended day, or null when <paramref name="day"/> is outside the calendar.</summary>
    public static (uint ItemId, ushort Count)? RewardOf(int day, int streakBrokenAfter)
    {
        if (day < 1 || day > Days) return null;
        var e = EntryFor(day, streakBrokenAfter);
        return e.ItemId == 0 || e.Count == 0 ? null : (e.ItemId, e.Count);
    }
}
