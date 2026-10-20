using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Byte-exact server-to-client builders for small economy acknowledgements whose body is a fixed frame or a frame that
/// holds a single scalar value.
/// </summary>
/// <remarks>
/// <para>
/// Every body starts with a compact-length marker of 1, 2, 4 or 8, which the client's compact decoder requires. A body
/// with any other first byte decodes as empty on the client and crashes its handler.
/// </para>
/// </remarks>
public static class EconomyMiscPackets
{
    /// <summary>Size in bytes of the u32 length prefix that opens a VarBlob32.</summary>
    private const int VarBlob32PrefixLength = 4;

    /// <summary>Inner length of the 2960 and 3029 bodies: one u32, or two u16 halves.</summary>
    private const uint SingleDwordBodyLength = 4;

    /// <summary>Result 0 of the siege-arms answers (2937, 2943): the request succeeded.</summary>
    private const ushort SiegeArmsResultSuccess = 0;

    /// <summary>Entry count n of a 2937 answer that carries no arm-skill cooldown entries.</summary>
    private const ushort SiegeArmsNoCooldownEntries = 0;

    /// <summary>
    /// Inner length of the 2937 success body with no cooldown entries: <c>[u16 result][u32 armHandle][u16 n]</c>, 8 bytes.
    /// </summary>
    private const uint SiegeArmsUseStartEmptyBodyLength = 2 + 4 + 2;






 /// <summary>
 /// 2926 AnsPCollectionIncubator. Layout <c>[CompactLen n][n x 58-byte incubator record][u16 result]</c>. The empty form,
 /// <c>01 00 | 00 00</c>, means that the account has no incubators.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The client acts only when the result is 0; any other value is dropped. The live sender is
 /// <see cref="PetIncubatorPackets.BuildAnsPCollectionIncubator"/> with the character's incubators; this is its empty form.
 /// </para>
 /// <para>
 /// It is sent right after the collection answer (2916) to the collection request.
 /// </para>
 /// </remarks>
    public static byte[] BuildPCollectionIncubatorEmptyAck() => PetIncubatorPackets.BuildAnsPCollectionIncubator([], DateTime.UtcNow);

 /// <summary>
 /// The Emergency Call's answer window in seconds, carried by the 2960 prompt. The client closes the window when the time
 /// runs out, and the server enforces the same value.
 /// </summary>
    public const uint GuildEmergencyCallAnswerSeconds = 10;

 /// <summary>
 /// 2960 AnsGuildEmergencyCallAsk: a VarBlob32 holding <c>[u32 len=4][u32 seconds]</c>. A server-initiated prompt for the
 /// guild skill Emergency Call, which summons every guild member to the guild master's position.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Sent by <c>GuildEmergencyCallService.IssueAsync</c> to every other online member when the master's skill lands. It is
 /// never sent in reply to 1653, which would reopen the prompt.
 /// </para>
 /// </remarks>
    public static byte[] BuildGuildEmergencyCallAsk()
    {
        using var w = new PacketWriter(8);
        w.WriteUInt32(SingleDwordBodyLength);
        w.WriteUInt32(GuildEmergencyCallAnswerSeconds);
        return w.ToArray();
    }

 /// <summary>
 /// 3029 AnsMakeOfferings: a VarBlob32 holding <c>[u32 len=4][u16 messageStringId][u16 errorCode]</c>, 8 bytes in total.
 /// </summary>
 /// <remarks>
 /// <para>
 /// <c>messageStringId</c> is the string table id shown after the offering. It comes from the world event row that matches
 /// the target NPC and the offered item, or is 0 when no row matches.
 /// </para>
 /// <para>
 /// <c>errorCode</c> is 0 on success, or <see cref="MakeOfferingsNotUsableNpc"/> for a target that is not an NPC. A non-zero
 /// <c>messageStringId</c> shows its message; otherwise <c>errorCode</c> is shown as a result message.
 /// </para>
 /// </remarks>
    public static byte[] BuildMakeOfferingsAck(ushort messageStringId, ushort errorCode = MakeOfferingsAccepted)
    {
        using var w = new PacketWriter(8);
        w.WriteUInt32(SingleDwordBodyLength);
        w.WriteUInt16(messageStringId);
        w.WriteUInt16(errorCode);
        return w.ToArray();
    }

 /// <summary>3029 error code 0: the offering is accepted.</summary>
    public const ushort MakeOfferingsAccepted = 0;

 /// <summary>
 /// 3029 error code for an offering whose target is not an NPC. The client shows a message that the target is not usable
 /// right now.
 /// </summary>
    public const ushort MakeOfferingsNotUsableNpc = 20055;



 /// <summary>
 /// 2942 NfySiegeArmsRotationStop: raw <c>[u32 handle][f32 yaw]</c>, 8 bytes. The yaw echoes the request; the handle is
 /// best-effort.
 /// </summary>
    public static byte[] BuildSiegeArmsRotationStop(uint siegeArmsHandle, float yaw)
    {
        using var w = new PacketWriter(8);
        w.WriteUInt32(siegeArmsHandle);
        w.WriteSingle(yaw);
        return w.ToArray();
    }

 /// <summary>
 /// 2941 NfySiegeArmsRotationStart: raw <c>[u32 handle][u8 direction]</c>, 5 bytes. The direction echoes the request; the
 /// handle is best-effort.
 /// </summary>
    public static byte[] BuildSiegeArmsRotationStart(uint siegeArmsHandle, byte direction)
    {
        using var w = new PacketWriter(5);
        w.WriteUInt32(siegeArmsHandle);
        w.WriteByte(direction);
        return w.ToArray();
    }

 /// <summary>
 /// 2937 AnsSiegeArmsUseStart: a VarBlob32 holding
 /// <c>[u32 innerLen][u16 result][u32 armHandle][u16 n][n x (u32 armSkillId, u32 remaining, u32 total)]</c>.
 /// </summary>
 /// <remarks>
 /// <para>
 /// This builder produces the form with no cooldown entries, a 12-byte success body. The live path builds the answer with
 /// its cooldown entries through the siege packet builder, which is identical to this form when nothing is cooling down.
 /// </para>
 /// </remarks>
    public static byte[] BuildSiegeArmsUseStartAck(uint siegeArmsHandle)
    {
        using var w = new PacketWriter(16);
        w.WriteUInt32(SiegeArmsUseStartEmptyBodyLength);
        w.WriteUInt16(SiegeArmsResultSuccess);
        w.WriteUInt32(siegeArmsHandle);
        w.WriteUInt16(SiegeArmsNoCooldownEntries);
        return w.ToArray();
    }

 /// <summary>
 /// 2943 AnsSiegeArmsUseSkill: raw <c>[u16 result][u32 armSkillId][f32 cooldown]</c>, 10 bytes. A result of 0 is a success
 /// that echoes the requested arm skill and its cooldown in seconds.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The refusal form, with result 20032 and zero skill and cooldown, is built by the siege packet builder and sent while
 /// the arm's cooldown group is still running. This builder produces the success form.
 /// </para>
 /// </remarks>
    public static byte[] BuildSiegeArmsUseSkillAck(uint armSkillId, float cooldownSeconds)
    {
        using var w = new PacketWriter(10);
        w.WriteUInt16(SiegeArmsResultSuccess);
        w.WriteUInt32(armSkillId);
        w.WriteSingle(cooldownSeconds);
        return w.ToArray();
    }



 /// <summary>Attendance entry flags: a plain reward day, with no bit of its own set.</summary>
    private const byte AttendanceFlagPlain = 0x00;

 /// <summary>Attendance entry flag 0x10: the diamond-pouch reward of a milestone day on an unbroken streak.</summary>
    private const byte AttendanceFlagPouch = 0x10;

 /// <summary>
 /// Item ids (ItemInfo, Item_Type 129) of the diamond pouch rewarded on milestone days 7, 14, 21 and 28 of an unbroken streak.
 /// </summary>
    private const uint DiamondPouchDay7 = 50001113, DiamondPouchDay14 = 50001114, DiamondPouchDay21 = 50001115, DiamondPouchDay28 = 50001116;

 /// <summary>
 /// Item ids (ItemInfo, Item_Type 127) of the seal rewarded on milestone days 7, 14, 21 and 28 once the streak has broken.
 /// </summary>
    private const uint SealDay7 = 22402012, SealDay14 = 22402013, SealDay21 = 22402014, SealDay28 = 22402015;

 /// <summary>Reward count of the seal milestone entry.</summary>
    private const byte AttendanceSealCount = 1;

 /// <summary>
 /// The 28-day attendance calendar of an unbroken cycle: for each day, the entry flag, the reward count and the reward item id.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The calendar does not rotate by month. Only the milestone days (7, 14, 21 and 28) differ between accounts: each shows
 /// either a pouch reward or a seal reward, decided by the account's streak (see <see cref="AttendanceMilestone"/>).
 /// </para>
 /// </remarks>
    private static readonly (byte Flag, byte Count, uint ItemId)[] AttendanceCalendar =
    {
        (AttendanceFlagPlain, 1, 60000165), (AttendanceFlagPlain, 1, 60000474), (AttendanceFlagPlain, 1, 60000486), (AttendanceFlagPlain, 1, 60000076),
        (AttendanceFlagPlain, 1, 60000277), (AttendanceFlagPlain, 1, 50001072), (AttendanceFlagPouch, 1, DiamondPouchDay7), (AttendanceFlagPlain, 1, 60000086),
        (AttendanceFlagPlain, 2, 60000474), (AttendanceFlagPlain, 2, 60000486), (AttendanceFlagPlain, 1, 60000076), (AttendanceFlagPlain, 1, 60000277),
        (AttendanceFlagPlain, 1, 50001072), (AttendanceFlagPouch, 1, DiamondPouchDay14), (AttendanceFlagPlain, 1, 60000058), (AttendanceFlagPlain, 3, 60000474),
        (AttendanceFlagPlain, 3, 60000486), (AttendanceFlagPlain, 1, 60000076), (AttendanceFlagPlain, 1, 60000277), (AttendanceFlagPlain, 1, 50001072),
        (AttendanceFlagPouch, 1, DiamondPouchDay21), (AttendanceFlagPlain, 1, 60000065), (AttendanceFlagPlain, 4, 60000474), (AttendanceFlagPlain, 4, 60000486),
        (AttendanceFlagPlain, 1, 60000076), (AttendanceFlagPlain, 1, 60000277), (AttendanceFlagPlain, 1, 50001072), (AttendanceFlagPouch, 1, DiamondPouchDay28),
    };

 /// <summary>The milestone days of the calendar: every seventh day (7, 14, 21 and 28).</summary>
    private const int AttendanceMilestoneEvery = 7;

 /// <summary>
 /// The milestone entry of a broken streak, for milestone k (day 7k) at index k-1: a seal reward with count 1 and flag 0.
 /// </summary>
    private static readonly uint[] AttendanceSealMilestones = { SealDay7, SealDay14, SealDay21, SealDay28 };

 /// <summary>The constant first byte of the attendance body, after the VarBlob32 length.</summary>
    private const byte AttendanceBodyMarker = 1;

 /// <summary>Length of the attendance body header: <c>[u8 marker][u8 days][u8 attendedDays][u16 n]</c>, 5 bytes.</summary>
    private const int AttendanceHeaderLength = 1 + 1 + 1 + 2;

 /// <summary>Length of one attendance entry: <c>[u8 flags][u8 count][u32 itemId]</c>, 6 bytes.</summary>
    private const int AttendanceEntryLength = 1 + 1 + 4;

 /// <summary>Lowest attendedDays value the client accepts; 0 is never sent.</summary>
    private const int AttendanceMinAttendedDays = 1;

 /// <summary>Entry bit for an attended day, set on the first <c>attendedDays</c> entries.</summary>
    private const byte AttendanceDayAttended = 0x20;

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
    internal static (byte Flag, byte Count, uint ItemId) AttendanceMilestone(int day, int streakBrokenAfter)
    {
        var entry = AttendanceCalendar[day - 1];
        if (day % AttendanceMilestoneEvery != 0 || streakBrokenAfter <= 0 || day <= streakBrokenAfter)
            return entry;
        return (AttendanceFlagPlain, AttendanceSealCount, AttendanceSealMilestones[day / AttendanceMilestoneEvery - 1]);
    }

 /// <summary>
 /// 3026 AnsShowAttendanceState (the 3027 notify has the same body): a VarBlob32 holding
 /// <c>[u32 innerLen][u8 1][u8 days][u8 attendedDays][u16 n][n x (u8 flags, u8 count, u32 itemId)]</c>, 177 bytes for the
 /// 28-day calendar.
 /// </summary>
 /// <remarks>
 /// <para>
 /// <c>flags</c> is the entry's own 0x10 bit (the pouch milestones of an unbroken streak) plus 0x20 on each attended day.
 /// </para>
 /// </remarks>
 /// <param name="attendedDays">Days attended in the cycle. Floored at 1, since the client cannot take 0.</param>
 /// <param name="streakBrokenAfter">The count attended before the first missed day, or 0 while the cycle is unbroken.</param>
    public static byte[] BuildShowAttendanceState(int attendedDays, int streakBrokenAfter)
    {
        var days = AttendanceCalendar.Length;
        var attended = Math.Clamp(attendedDays, AttendanceMinAttendedDays, days);
        using var w = new PacketWriter(VarBlob32PrefixLength + AttendanceHeaderLength + AttendanceEntryLength * days);
        w.WriteUInt32((uint)(AttendanceHeaderLength + AttendanceEntryLength * days));
        w.WriteByte(AttendanceBodyMarker);
        w.WriteByte((byte)days);
        w.WriteByte((byte)attended);
        w.WriteUInt16((ushort)days);
        for (var i = 0; i < days; i++)
        {
            var e = AttendanceMilestone(i + 1, streakBrokenAfter);
            w.WriteByte((byte)(e.Flag | (i < attended ? AttendanceDayAttended : 0)));
            w.WriteByte(e.Count);
            w.WriteUInt32(e.ItemId);
        }
        return w.ToArray();
    }
}
