using System.Collections.Generic;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the quest, talent and profession S2C notifies, and the result codes they carry.
/// </summary>
/// <remarks>
/// Every body uses the compact-BA envelope <c>[compact marker 0x01][u8 selfLen][u16 selfLen][u16 msgTag][fields...]</c>,
/// written through <see cref="PacketWriter.WriteCompactBABody"/>. The payload starts with the u16 msgTag. The client's
/// blob begins at the u16 selfLen, so the first field sits at blob+4. Offsets in member remarks are relative to the
/// body after the frame header.
/// </remarks>
public static class QuestProgressPackets
{
    // msgTags: the u16 at blob+2 of each body (payload offset 0), one per RMI.
    internal const ushort MsgTagQuestGiveup = 0xE3B8;       // 58296, RMI 2420, body length 19
    internal const ushort MsgTagQuestInsert = 0xE3B5;       // 58293, RMI 2421, body length 18
    internal const ushort MsgTagQuestComplete = 0xE3BC;     // 58300, RMI 2427, body length 18
    internal const ushort MsgTagQuestMonster = 0xE3DB;      // 58331, RMI 2425, body length 20
    internal const ushort MsgTagQuestPrize = 0xE3BF;        // 58303, RMI 2429, body length 30
    internal const ushort MsgTagQuestError = 0xE3C3;        // 58307, RMI 2431, body length 22
    internal const ushort MsgTagUseTalent = 0xE40F;         // 58383, RMI 2543, variable length (17 + 5 * count)
    internal const ushort MsgTagInitTalent = 0xE411;        // 58385, RMI 2544, body length 13
    internal const ushort MsgTagBonusStat = 0xE437;         // 58423, RMI 2680, body length 32
    internal const ushort MsgTagChangeProJob = 0xE43A;      // 58426, RMI 2682, body length 16
    internal const ushort MsgTagChangeProJobInfo = 0xE439;  // 58425, RMI 2683, body length 21

    // 2431 OnQuestError result codes (u32 at blob+12). They are server result ids, not client string ids: the client
    // special-cases only QuestErrorRequirementUnmet (21712) and hands every other code to its result-message dispatcher.

    /// <summary>
    /// Refusal of a 1374 accept for a quest the character already holds. It resolves to an empty string, so the client
    /// shows nothing and the quest is not added. A finished one-shot quest gets no answer (QuestAcceptHandler).
    /// </summary>
    internal const uint QuestErrorInsertRejected = 21703;

    /// <summary>
    /// Refusal of a 1378 turn-in for a quest that was already paid earlier in the same session. A quest neither held
    /// nor paid in the login gets no answer (QuestPrizeHandler).
    /// </summary>
    internal const uint QuestErrorPrizeRejected = 21711;

    /// <summary>
    /// The "objective not met" refusal, which shows the requirement message. It is sent for a talk-quest turn-in at the
    /// Reward NPC when the quest's first talk NPC has not been met in this login. The quest stays in the client's log.
    /// </summary>
    internal const uint QuestErrorRequirementUnmet = 21712;

    /// <summary>
    /// Refusal of a 1374 for a daily quest already completed since the last daily reset (<see cref="QuestRepeatGate"/>).
    /// The 2431 carries the row's Daily_Quest value as its flag.
    /// </summary>
    internal const uint QuestErrorDailyDone = 21717;

    /// <summary>
    /// Refusal of a 1374 for a quest whose PC_Job mask does not include the character's combat class. It is the whole
    /// answer (QuestAcceptGates).
    /// </summary>
    internal const uint QuestErrorJobMismatch = 21718;

    /// <summary>
    /// Refusal of a 1374 for a quest whose PC_ProJob is not the character's profession. It is the whole answer
    /// (QuestAcceptGates).
    /// </summary>
    internal const uint QuestErrorProJobMismatch = 21719;

    /// <summary>
    /// Refusal of a 1374 whose level is outside the quest's Job_Master range. The range is Min &lt;= level &lt; Max, where
    /// Max 0 means no upper bound. The level is the master level or the base level, depending on Job_Master_Quest. This
    /// check runs before <see cref="QuestErrorJobMismatch"/>.
    /// </summary>
    internal const uint QuestErrorJobMasterLevel = 21702;

    /// <summary>
    /// Refusal of a 1378 whose prize cannot be handed over: a required completion item is missing from the bag, or the
    /// reward items do not fit in it.
    /// </summary>
    internal const uint QuestErrorPrizeNotGiven = 21708;

    /// <summary>
    /// The bag-full code, as used by 2375, 2801 and 2438: a 1378 whose reward items do not fit, or a 1374 whose accept
    /// items do not fit.
    /// </summary>
    internal const uint QuestErrorBagFull = 20020;

    /// <summary>
    /// The 2421 result of a 1374 for a quest that takes an item at accept, when the bag does not hold that item. The quest
    /// is not in play afterwards.
    /// </summary>
    internal const uint QuestInsertItemMissing = 20021;

    /// <summary>
    /// The 2421 result of a 1374 for a Start_Type 2 quest when the bag does not hold the item the quest starts from. The
    /// quest is not added. This check runs after <see cref="QuestErrorJobMasterLevel"/>.
    /// </summary>
    internal const uint QuestInsertStartItemMissing = 20036;

    /// <summary>The 2379 OnDestroyItem msgTag (0xE2C7, u16 at blob+2).</summary>
    internal const ushort MsgTagDestroyItem = 0xE2C7; // 58055

    /// <summary>The 2379 result at blob+8: 0 = success. A non-zero result is routed to the error handler.</summary>
    private const ushort DestroyItemResultOk = 0;

    /// <summary>
    /// The 2379 reason at blob+10 for quest turn-ins and card compositions, which is also the value written by
    /// <see cref="BuildItemRemoved2379"/>.
    /// </summary>
    private const ushort ItemRemovedReasonDefault = 0;

    /// <summary>
    /// RMI 2379 OnDestroyItem with an item record. Removes quest items, or part of a stack, from the bag.
    /// </summary>
    /// <remarks>
    /// Blob layout: [u16 selfLen][u16 msgTag][u32 self PC handle][u16 result = 0][u16 reason = 0][ItemRecord126].
    /// The record's count is the amount the stack holds afterwards: 0 empties the cell, and any other value sets the
    /// stack to that count. A non-zero result is routed to the error handler.
    /// </remarks>
    public static byte[] BuildItemRemoved2379(uint actorHandle, ReadOnlySpan<byte> record126)
        => BuildItemRemovedWithReason2379(actorHandle, ItemRemovedReasonDefault, record126);

    /// <summary>
    /// RMI 2379 OnDestroyItem with an explicit reason code at blob+10. The reason says why the stack left the bag: 0 for
    /// quest turn-ins and card compositions, 5 for a card-book put, and 1 for a guild-storage put.
    /// </summary>
    public static byte[] BuildItemRemovedWithReason2379(uint actorHandle, ushort reason, ReadOnlySpan<byte> record126)
    {
        var record = record126.ToArray();
        using var pw = new PacketWriter(160);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDestroyItem);
            p.WriteUInt32(actorHandle);          // blob+4  self PC handle
            p.WriteUInt16(DestroyItemResultOk);  // blob+8  result
            p.WriteUInt16(reason);               // blob+10 why the stack left the bag
            p.WriteBytes(record);                // blob+12 the stack's record, count = what is left
        });
        return pw.ToArray();
    }

    /// <summary>2379 reason code at blob+10 of a card-book put (1546).</summary>
    internal const ushort ItemRemovedReasonCardBookPut = 5;

    /// <summary>
    /// RMI 2420 OnQuestGiveup. Body 19, payload 13: a u32 zero, the u32 quest id, then 5 zero bytes.
    /// </summary>
    public static byte[] BuildQuestGiveup2420(uint questId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestGiveup);
            p.WriteUInt32(0);        // body+6  reserved
            p.WriteUInt32(questId);  // body+10 Quest_Info key
            p.WriteUInt32(0);        // body+14 reserved
            p.WriteByte(0);          // body+18 reserved
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2421 OnQuestInsert. Body 18, payload 12: a u32 zero, the u32 quest id, then a u32 zero (the result).
    /// </summary>
    public static byte[] BuildQuestInsert2421(uint questId) => BuildQuestInsertResult2421(questId, QuestInsertResultInserted);

    /// <summary>The 2421 result 0: the quest was inserted.</summary>
    private const uint QuestInsertResultInserted = 0;

    /// <summary>
    /// RMI 2421 with a result code at body+14 (blob+12). 0 inserts the quest. Any other value refuses the 1374, and
    /// the quest is not added.
    /// </summary>
    public static byte[] BuildQuestInsertResult2421(uint questId, uint result)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestInsert);
            p.WriteUInt32(0);        // body+6  reserved
            p.WriteUInt32(questId);  // body+10 Quest_Info key
            p.WriteUInt32(result);   // body+14 result: 0 = inserted
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2427 OnQuestComplete. Body 18, the same shape as OnQuestInsert 2421 (msgTag 58300). It is the completion
    /// acknowledgement that lets the client close the quest in its tracker.
    /// </summary>
    public static byte[] BuildQuestComplete2427(uint questId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestComplete);
            p.WriteUInt32(0);        // body+6  reserved
            p.WriteUInt32(questId);  // body+10 Quest_Info key
            p.WriteUInt32(0);        // body+14 reserved
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2425 OnQuestMonsterOrGO. Body 20, payload 14: a u32 zero, the u32 quest id, the u32 objective target, and a
    /// u16 running count.
    /// </summary>
    /// <remarks>
    /// The count is a u16, so counts above 255 are sent correctly.
    /// </remarks>
    public static byte[] BuildQuestMonsterOrGo2425(uint questId, uint monsterId, ushort progressCount)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestMonster);
            p.WriteUInt32(0);             // body+6  reserved
            p.WriteUInt32(questId);       // body+10 Quest_Info key
            p.WriteUInt32(monsterId);     // body+14 objective target id (Complete_Type_Value1 of the quest row)
            p.WriteUInt16(progressCount); // body+18 running kill count
        });
        return pw.ToArray();
    }

    /// <summary>The 2429 result 0: the prize was paid.</summary>
    private const uint QuestPrizeResultPaid = 0;

    /// <summary>
    /// RMI 2429 OnQuestPrize. Body 30 (body offset = blob offset + 2): a u32 zero, a u32 result (0 = paid), the u32
    /// quest id, then the reward EXP, money and diamonds.
    /// </summary>
    /// <remarks>
    /// The reward fields come from the quest's Quest_Info row. The client shows them as reward messages and adds the EXP
    /// to its own counter. The wallet is set by the 2435 that follows, so the money field is a delta, not a balance. A
    /// non-zero result is routed to the error handler. All zeros is the no-reward case.
    /// </remarks>
    public static byte[] BuildQuestPrize2429(uint questId, uint rewardExp = 0, uint rewardMoney = 0,
        uint rewardDiamonds = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestPrize);
            p.WriteUInt32(0);                    // blob+4  reserved
            p.WriteUInt32(QuestPrizeResultPaid); // blob+8  result
            p.WriteUInt32(questId);              // blob+12 Quest_Info key
            p.WriteUInt32(rewardExp);            // blob+16 Reward_EXP (the client adds it to its EXP)
            p.WriteUInt32(rewardMoney);          // blob+20 Reward_Money (display; the 2435 carries the balance)
            p.WriteUInt32(rewardDiamonds);       // blob+24 Reward_Dia (display)
        });
        return pw.ToArray();
    }

    /// <summary>The 2431 flag byte at blob+16 when the code carries no flag.</summary>
    private const byte QuestErrorFlagNone = 0;

    /// <summary>
    /// RMI 2431 OnQuestError. Body 22, payload 16: a u32 zero, the u32 quest id, the u32 result code, a flag byte, and
    /// padding. The result code is one of the QuestError* ids.
    /// </summary>
    public static byte[] BuildQuestError2431(uint questId, uint errorCode) => BuildQuestError2431(questId, errorCode, QuestErrorFlagNone);

    /// <summary>
    /// RMI 2431 with the flag byte set. The flag is used only with <see cref="QuestErrorDailyDone"/>, where it carries
    /// the quest's Daily_Quest value.
    /// </summary>
    public static byte[] BuildQuestError2431(uint questId, uint errorCode, byte flag)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestError);
            p.WriteUInt32(0);          // body+6  (blob+4)  reserved
            p.WriteUInt32(questId);    // body+10 (blob+8)  Quest_Info key
            p.WriteUInt32(errorCode);  // body+14 (blob+12) result code (a QuestError* id)
            p.WriteByte(flag);         // body+18 (blob+16) flag: 0, or Daily_Quest with QuestErrorDailyDone
            p.WriteByte(0);            // body+19..21 (blob+17..19) padding
            p.WriteUInt16(0);
        });
        return pw.ToArray();
    }

    /// <summary>The 2543 and 2544 result at blob+8: 0 = success.</summary>
    private const ushort TalentResultOk = 0;

    /// <summary>
    /// RMI 2543 OnUseTalentPoint, a variable-length vector echo of the client's allocation request. Body: a u32 zero, a
    /// u16 zero, a remaining-points byte, a u32 count, then count elements of {u32 id, u8 level}.
    /// </summary>
    /// <remarks>
    /// The body length is 17 + 5 * count. The element list is echoed from the request, so the ids and levels are the
    /// player's actual allocations.
    /// </remarks>
    public static byte[] BuildUseTalentPoint2543(
        IReadOnlyList<(uint Id, byte Level)> allocations, byte remainingPoints)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUseTalent);
            p.WriteUInt32(0);                        // blob+4  reserved
            p.WriteUInt16(TalentResultOk);           // blob+8  result (the client shows an error popup if non-zero)
            p.WriteByte(remainingPoints);            // blob+10 new remaining points; the client sets its counter to it
            p.WriteUInt32((uint)allocations.Count);  // blob+11 element count
            foreach (var (id, level) in allocations) // blob+15 count x {u32 id, u8 level}
            {
                p.WriteUInt32(id);
                p.WriteByte(level);
            }
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2544 OnInitTalentPoint. Body 13: the u32 pc handle, a u16 zero, then the remaining-points byte.
    /// </summary>
    public static byte[] BuildInitTalentPoint2544(uint pcHandle, byte remainingPoints)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagInitTalent);
            p.WriteUInt32(pcHandle);       // blob+4  self PC handle (not read by the client)
            p.WriteUInt16(TalentResultOk); // blob+8  result
            p.WriteByte(remainingPoints);  // blob+10 new talent-point total (full refund after a reset)
        });
        return pw.ToArray();
    }

    // 2680 result codes (u16 at blob+9). The client reads only this word and shows an error for a non-zero value.
    internal const ushort BonusStatResultOk = 0;            // applied
    internal const ushort BonusStatResultBadState = 1;      // change requested in an unchangeable state
    internal const ushort BonusStatResultOverCap = 2;       // exceeds the range of the basic stats
    internal const ushort BonusStatResultTooFewPoints = 3;  // not enough bonus points

    /// <summary>Length of the 1493 request echo carried by the 2680 body.</summary>
    internal const int BonusStatEchoLength = 19;

    /// <summary>
    /// RMI 2680 OnAnsUseBonusStatPoint. Body 32: the u32 pc handle, a zero byte, the u16 result, then a 19-byte echo of
    /// the 1493 request.
    /// </summary>
    /// <remarks>
    /// Only the result is read by the client (see the BonusStatResult constants). The echo is carried for compatibility.
    /// </remarks>
    public static byte[] BuildBonusStatPoint2680(uint pcHandle, ushort result, ReadOnlySpan<byte> requestEcho)
    {
        var echo = new byte[BonusStatEchoLength];
        requestEcho[..Math.Min(requestEcho.Length, BonusStatEchoLength)].CopyTo(echo);
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBonusStat);
            p.WriteUInt32(pcHandle);  // blob+4  requester's PC handle
            p.WriteByte(0);           // blob+8  reserved
            p.WriteUInt16(result);    // blob+9  result code (a BonusStatResult* value), the only field the client reads
            p.WriteBytes(echo);       // blob+11 the request's blob+11..29, verbatim
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2682 NfyChangeProJob, which tells other players that an actor has a new profession and level. Body 16: a u32
    /// zero, the u32 subject PC handle, the u8 profession id, and the u8 profession level.
    /// </summary>
    /// <remarks>
    /// The receiving client applies the profession to the subject actor. The subject itself does not receive this message.
    /// </remarks>
    public static byte[] BuildChangeProJob2682(uint subjectHandle, byte proJobId, byte proJobLevel)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagChangeProJob);
            p.WriteUInt32(0);              // blob+4  reserved
            p.WriteUInt32(subjectHandle);  // blob+8  PC handle of the actor whose profession changed
            p.WriteByte(proJobId);         // blob+12 profession id
            p.WriteByte(proJobLevel);      // blob+13 profession level
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2683 AnsChangeProJobInfo, the answer to 1494 ReqChangeProJobID. Carries the character's profession block (the
    /// same fields as in the 2310 login body) and a result byte. Body 21.
    /// </summary>
    /// <remarks>
    /// A non-zero result is shown as an error and nothing else changes. On 0 the client stores the new profession, its
    /// level and its experience in the character record. Body layout: [u16 19][u16 msgTag][u32 0][u8 profession]
    /// [u8 level][u64 exp][u8 result].
    /// </remarks>
    /// <param name="proJobType">The ProJob_Type id of the profession.</param>
    /// <param name="level">The profession level.</param>
    /// <param name="exp">The profession experience.</param>
    /// <param name="result">0 when the change was applied, otherwise one of the ChangeProJob refusal codes.</param>
    public static byte[] BuildChangeProJobInfo2683(byte proJobType, byte level = 0, ulong exp = 0, byte result = ChangeProJobApplied)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagChangeProJobInfo);
            p.WriteUInt32(0);          // blob+4  reserved
            p.WriteByte(proJobType);   // blob+8  ProJob_Type id
            p.WriteByte(level);        // blob+9  profession level
            p.WriteUInt64(exp);        // blob+10 profession experience
            p.WriteByte(result);       // blob+18 result, 0 = applied
        });
        return pw.ToArray();
    }

    // 2683 result codes (u8 at blob+18). A non-zero code is shown to the player as a notification and nothing else changes.

    /// <summary>2683 result 0: the profession was changed.</summary>
    internal const byte ChangeProJobApplied = 0;

    /// <summary>2683 result 1: the target is not a known profession id.</summary>
    internal const byte ChangeProJobUnknownProfession = 1;

    /// <summary>2683 result 3: the target is the character's current profession.</summary>
    internal const byte ChangeProJobSameProfession = 3;

    /// <summary>2683 result 4: the required profession-change item is not in the bag.</summary>
    internal const byte ChangeProJobMissingItem = 4;

    /// <summary>Body length of the 2687 notify: two u32 quest ids, with no envelope.</summary>
    private const int ChangeProJobQuestBodyLength = 2 * sizeof(uint);

    /// <summary>
    /// RMI 2687 NfyChangeProJobQuest. A fixed 8-byte body, [u32 removed quest][u32 added quest]: one of the old
    /// profession's guide quests is replaced by the new profession's.
    /// </summary>
    /// <remarks>
    /// Sent between the 2379 and the 2683 of a profession change.
    /// </remarks>
    public static byte[] BuildChangeProJobQuest2687(uint removedQuestId, uint addedQuestId)
    {
        var b = new byte[ChangeProJobQuestBodyLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, removedQuestId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(sizeof(uint)), addedQuestId);
        return b;
    }

    /// <summary>Length of the 2686 blob: two u16 element counts, both zero.</summary>
    private const uint GuardianSkillBlobLength = 2 * sizeof(ushort);

    /// <summary>
    /// RMI 2686 NfyChageProJobGuardianSkill. Tells the client to request its skill list again after a profession change.
    /// Body 8: a u32 length of 4, then two empty vectors.
    /// </summary>
    /// <remarks>
    /// Part of the answer to 1494 ReqChangeProJobID, sent after the 2687s. The client's consumer does not read the
    /// vectors. Its action is to send 1367 ReqUseSkillList, which brings back the 2488 skill list.
    /// </remarks>
    public static byte[] BuildChageProJobGuardianSkill2686()
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(GuardianSkillBlobLength);  // blob length: the two u16 vector counts below
        pw.WriteUInt16(0);                        // first vector: 0 elements
        pw.WriteUInt16(0);                        // second vector: 0 elements
        return pw.ToArray();
    }
}
