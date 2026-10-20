using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the combat, progression and interaction notifies.
/// </summary>
/// <remarks>
/// <para>
/// Every opcode here must be sent inside the compact-length envelope: the compact-length decoder accepts only
/// marker bytes {1,2,4,8}, and when it rejects a body the per-opcode stub hands the consumer a NULL blob that it
/// dereferences without a null check. A raw body is therefore a remote client crash rather than a dropped packet.
/// </para>
/// <para>
/// Each layout below documents the field offsets the client reads. The regression tests reproduce known-good
/// frames byte for byte.
/// </para>
/// <para>
/// ⚠️ <b>Where the payload starts is NOT uniform.</b> Most of this protocol leaves blob+4 as a
/// constant zero and begins fields at blob+8, but 2394, 2329 and 2396 put the actor handle at
/// blob+4. Two adjacent opcodes, 2394 and 2395, disagree on this. Assuming either shape shifts
/// every field by four bytes.
/// </para>
/// </remarks>
public static class CombatPackets
{
    // ---- 2696 NfyAttackDamage ---------------------------------------------------------------

    internal const ushort MsgTagAttackDamage = 58431; // 0xE43F
    internal const int AttackDamageBodyLen = 52;

    /// <summary>2696 blob+8 attack motion category of a normal attack; always 1.</summary>
    internal const byte AttackMotionNormal = 1;

    /// <summary>2696 blob+9 damage kind of a normal hit (see <see cref="DamageKindMiss"/> for the others).</summary>
    internal const byte DamageKindNormal = 1;

    /// <summary>
    /// 2696 blob+9 damage kind of a MISS: no damage, and no weapon wear. Kinds 1 (normal), 3 (guard)
    /// and 4 (crit) all land.
    /// </summary>
    internal const byte DamageKindMiss = 2;

    /// <summary>
    /// Builds RMI 2696 NfyAttackDamage — one normal-attack hit. 52 bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ The two 12-byte actor records begin at blob+26 and blob+38, so every handle, HP and SP in
    /// this message is 2-mod-4 MISALIGNED. Do not tidy it onto a u32 grid. SP is <b>u16</b>.
    /// </para>
    /// <para>
    /// The victim HP is the value AFTER this hit. The roles are attacker@26/victim@38, matching the 2694 that
    /// precedes it.
    /// </para>
    /// <para>
    /// blob+18 and blob+22 are constant zero but ARE read by the client into a combat record. 2718 fills the
    /// same slots from a skill context, which suggests a skill id pair a normal attack leaves empty; that is not
    /// asserted, and zero is what is sent.
    /// </para>
    /// </remarks>
    public static byte[] BuildAttackDamage2696(
        uint attackerHandle, uint attackerCurHp, ushort attackerCurSp,
        uint victimHandle, uint victimCurHpAfter, ushort victimCurSp,
        uint damage, byte damageKind = DamageKindNormal)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAttackDamage); // blob+2
            p.WriteUInt32(0);                  // blob+4  never read
            p.WriteByte(AttackMotionNormal);      // blob+8  attack motion category
            p.WriteByte(damageKind);           // blob+9  1 hit, 2 no damage
            p.WriteUInt32(damage);             // blob+10
            p.WriteUInt32(0);                  // blob+14 never read
            p.WriteUInt32(0);                  // blob+18 read, but always zero
            p.WriteUInt32(0);                  // blob+22 read, but always zero
            p.WriteUInt32(attackerHandle);     // blob+26 type-tagged handle
            p.WriteUInt32(attackerCurHp);      // blob+30
            p.WriteUInt16(attackerCurSp);      // blob+34 u16, NOT u32
            p.WriteUInt16(0);                  // blob+36
            p.WriteUInt32(victimHandle);       // blob+38
            p.WriteUInt32(victimCurHpAfter);   // blob+42 AFTER the hit
            p.WriteUInt16(victimCurSp);        // blob+46 u16
            p.WriteUInt16(0);                  // blob+48
        });
        return pw.ToArray();
    }

    // ---- 2694 NfyNormalAttackStart ---------------------------------------------------------

    internal const ushort MsgTagAttackStart = 58430; // 0xE43E
    internal const int AttackStartBodyLen = 22;

    /// <summary>
    /// 2694 blob+8: a u32 that is always 1. What it selects is not established (plausibly a hand or hit index).
    /// </summary>
    internal const uint AttackStartConstant = 1;

    /// <summary>
    /// Builds RMI 2694 NfyNormalAttackStart — the swing itself, as opposed to the damage it deals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE SERVER WAS SENDING THE WRONG OPCODE. It broadcast 2695 with an 8-byte raw body. The swing is 2694, with
    /// a 22-byte body and the standard envelope, sent roughly once per swing; 2695 is a different and much rarer
    /// message (the attack refusal).
    /// </para>
    /// <para>
    /// Layout: <c>[compactLen 01 14][u16 selfLen=20][u16 msgTag=0xE43E][u32 0][u32 1]
    /// [u32 attackerHandle][u32 targetHandle]</c>.
    /// </para>
    /// <para>
    /// The two handles carry their type tags, and in consecutive frames they SWAP — a PC
    /// handle and an NPC handle trade places — which reads as the two directions of an exchange of
    /// blows. That also makes this the message a monster needs in order to visibly swing back.
    /// </para>
    /// <para>
    /// ⚠️ The u32 at blob+8 is always 1 and is written as 1. What it selects is
    /// NOT established — plausibly a hand or hit index — so it is not modelled, only reproduced.
    /// </para>
    /// </remarks>
    public static byte[] BuildAttackStart2694(uint attackerHandle, uint targetHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAttackStart); // blob+2
            p.WriteUInt32(0);                 // blob+4
            p.WriteUInt32(AttackStartConstant); // blob+8  constant
            p.WriteUInt32(attackerHandle);    // blob+12
            p.WriteUInt32(targetHandle);      // blob+16
        });
        return pw.ToArray();
    }

    // ---- 2695 AnsErrorNormalAttack ---------------------------------------------------------

    /// <summary>
    /// Refusal code for a target that is DEAD: the code used when the target died before the
    /// request was answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusal codes in use are 20122, 20158, 22301, 20014, 20119 and 20151, message ids the
    /// client passes on for the error toast; it reads nothing else. The two main ones separate by whether the
    /// target was dead: 20122 is the dead-target code (a 2392 OnDeadNotify naming the 1498 target as its victim
    /// came before the refusal) and 22301 is the usual code for refusing a target that is still alive.
    /// </para>
    /// <para>
    /// The remaining four (20158, 20014, 20119, 20151) are not separated: 20158 comes mostly after
    /// 1499, the damage request, and the others are too rare to characterise.
    /// </para>
    /// </remarks>
    internal const ushort AttackRefusedTargetDead = 20122;

    /// <summary>
    /// Refusal code for a target that is NOT dead: 22301 (see <see cref="AttackRefusedTargetDead"/>).
    /// </summary>
    /// <remarks>
    /// Which live-target condition it stands for is not established: the target is an attackable monster, and
    /// nothing else in the surrounding frames says why. It is the code for "refused, target alive", not for any
    /// one reason. This server uses it for the refusals that are not a death.
    /// </remarks>
    internal const ushort AttackRefusedLiveTarget = 22301;

    /// <summary>
    /// The builder's default code: the dead-target one. The live caller always passes a code chosen from the
    /// refusal reason.
    /// </summary>
    internal const ushort AttackRefusedCode = AttackRefusedTargetDead;

    /// <summary>Body length of 2695: 14 bytes.</summary>
    internal const int AttackRefusedBodyLen = 14;

    /// <summary>Zero bytes after the u16 refusal code in 2695 (the body is 14 bytes: 2 + 12).</summary>
    private const int AttackRefusedPadLen = AttackRefusedBodyLen - sizeof(ushort);

    /// <summary>
    /// Builds RMI 2695 AnsErrorNormalAttack — the refusal that takes the client OUT of the attack
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ THIS OPCODE HAS NO COMPACT ENVELOPE. The body is a bare <c>[u16 code][12 zero bytes]</c>, and only six
    /// distinct bodies exist. Wrapping it in the envelope the rest of this family uses would be wrong.
    /// </para>
    /// <para>
    /// Sending this was once a bug — the server broadcast a literal 2695 with a handle pair as the
    /// body on every engage, so the client read the low half of a handle as a message id and showed
    /// a random error. Sent correctly, on an actual refusal, it is how the client learns to stop:
    /// without it a granted swing has no way to end, which showed up live as the character sticking
    /// in the attack animation after arriving at a target, and breaking when an attack was
    /// interrupted by walking away.
    /// </para>
    /// </remarks>
    public static byte[] BuildAttackRefused2695(ushort code = AttackRefusedCode)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(code);
        pw.WriteZeros(AttackRefusedPadLen);
        return pw.ToArray();
    }

    // ---- 2398 OnChangeState ----------------------------------------------------------------

    internal const ushort MsgTagChangeState = 58090; // 0xE2EA
    internal const int ChangeStateBodyLen = 29;

    /// <summary>Actor state values.</summary>
    public static class ActorState
    {
        /// <summary>A player character's normal state, sent after each of its hits.</summary>
        public const byte PcNormal = 0;

        /// <summary>A monster leaving combat.</summary>
        public const byte NpcLeaveCombat = 1;

        /// <summary>
        /// A monster giving up a fight it did not die in: the first of the two frames that close the engagement
        /// (state 5, then state 1 a few hundred milliseconds later).
        /// </summary>
        /// <remarks>
        /// An engagement opened by a state 3 with blob+15 = 0 and blob+19 = 3 (NpcChatService.EnterCombatAsync's
        /// form) ends in the NPC's 2392, in a 2507 out-list, or in a state 5. A state 5 carries blob+14 = 0,
        /// blob+19 = 3 and target 0, and is normally followed by a state 1 with the same blob+19 and target.
        /// </remarks>
        public const byte NpcReturn = 5;

        /// <summary>A monster entering combat.</summary>
        public const byte NpcEnterCombat = 3;

        /// <summary>
        /// A player character's weapon-drawn COMBAT stance ("in battle"). Sent as the 2398 state byte (blob+12)
        /// for a PC subject; the client transitions the actor into its combat animation. 0x0C is the
        /// common-weapon stance, a per-character constant, with 0x0D/0x0E/0x11/0x13 the minority weapon/class
        /// stances. The server enters it on the player's first attack given or hit taken, re-pulses it per combat
        /// action (~1.5 s), and returns to <see cref="PcNormal"/> after a ~3-5 s out-of-combat timeout
        /// (PcCombatStateService). Leaving it per-hit was the stuck-run bug; re-pulsing the stance itself is
        /// correct.
        /// </summary>
        public const byte PcCombat = 0x0C;
    }

    /// <summary>
    /// Builds RMI 2398 OnChangeState — the actor state notify, sent at high volume (roughly twice as often as
    /// 2694).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of these go out immediately after every 2696: a PC subject with state 0, and an NPC subject with
    /// states 3, 5 and 1.
    /// </para>
    /// <para>
    /// ⚠️ <c>blob+4</c> follows a CONDITIONAL rule: when the subject is a <b>PC</b> it MIRRORS blob+8; when the
    /// subject is an <b>NPC</b> it is ZERO. This is the "payload does not always start at blob+8" trap, and
    /// here the two cases are exactly complementary.
    /// </para>
    /// <para>
    /// ⚠️ The state byte feeds a 21-case switch in the client, so an invented value is an
    /// out-of-range index. Only known values are exposed, via <see cref="ActorState"/>. Directionally: NPC 3
    /// enters combat, NPC 1 and 5 leave it, NPC 2 is not fighting at all.
    /// </para>
    /// <para>
    /// blob+13, +15 and +19 are NOT modelled. The frames carry a small integer at
    /// blob+19 — 2 for the PC subject and 3 for the NPC — which is reproduced without any claim
    /// about what it means.
    /// </para>
    /// <para>
    /// blob+14 (<paramref name="combatFlag"/>) behaves like an in-combat flag on an NPC subject: every NPC
    /// state-3 (enter combat) frame carries 1, and every NPC state 1, 2 and 5 frame carries 0. So the
    /// enter-combat sender (NpcChatService.EnterCombatAsync) passes 1 and every other NPC sender
    /// keeps the default 0. PC subjects also carry 1 in some frames by a rule not established, so PC builders
    /// keep 0.
    /// </para>
    /// </remarks>
    public static byte[] BuildChangeState2398(uint subjectHandle, byte state,
        uint targetHandle = 0, uint trailing = 0, byte combatFlag = 0)
    {
        var isPc = (subjectHandle & 0xFF) == PlayerStateManager.PcActorTag;
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagChangeState);            // blob+2
            p.WriteUInt32(isPc ? subjectHandle : 0u);    // blob+4  mirror for a PC, zero for an NPC
            p.WriteUInt32(subjectHandle);                // blob+8
            p.WriteByte(state);                          // blob+12
            p.WriteByte(0);                              // blob+13 unmodelled
            p.WriteByte(combatFlag);                     // blob+14 1 for NPC state 3, else 0 (remarks)
            p.WriteUInt32(0);                            // blob+15 unmodelled
            p.WriteUInt32(trailing);                     // blob+19 reproduced, not modelled
            p.WriteUInt32(targetHandle);                 // blob+23 zero when there is none
        });
        return pw.ToArray();
    }

    // ---- 2643 AnsActorIconInfo -------------------------------------------------------------

    /// <summary>Body length of 2643: 21 bytes.</summary>
    internal const int ActorIconBodyLen = 21;

    /// <summary>
    /// Builds RMI 2643 AnsActorIconInfo — the target panel, answering C2S 1307 ReqActorDetailInfo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: <c>[u32 targetHandle][u32 hp][u32 maxHp][u16 sp][u16 maxSp][u8 level][u32 targetOfTarget]</c>, 21 bytes.
    /// </para>
    /// <para>
    /// ⚠️ NO ENVELOPE. The body begins with the target handle, whose low byte is the 0x13 actor tag,
    /// not a compact-length marker.
    /// </para>
    /// <para>
    /// ⚠️ The HP/SP/level reading is STRONG BUT NOT PROVEN. The field ORDER is inferred from full-health
    /// samples (cur == max); if the panel shows wrong numbers for a wounded target, this is the place to look.
    /// </para>
    /// <para>
    /// ⚠️ <b>body+16 is a u8 level and body+17 an UNALIGNED u32 actor handle</b>, not a u32 level plus
    /// a pad byte. body+17 is the TARGET'S OWN TARGET: zero or a tagged handle (0x11 PC, 0x13 NPC). For an NPC
    /// target it is zero or the PC the monster is on; for a PC target it is whatever that PC has selected.
    /// Writing the level as a u32 put zeros over it, so the client never saw a target-of-target.
    /// </para>
    /// </remarks>
    public static byte[] BuildActorIconInfo2643(uint targetHandle, uint hp, uint maxHp,
        ushort sp, ushort maxSp, uint level, uint targetOfTarget = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(targetHandle);                  // body+0
        pw.WriteUInt32(hp);                            // body+4
        pw.WriteUInt32(maxHp);                         // body+8
        pw.WriteUInt16(sp);                            // body+12
        pw.WriteUInt16(maxSp);                         // body+14
        pw.WriteByte((byte)Math.Min(level, byte.MaxValue)); // body+16 u8
        pw.WriteUInt32(targetOfTarget);                // body+17 unaligned u32, 0 = none
        return pw.ToArray();
    }

    // ---- 2394 OnLevelUp (self) -------------------------------------------------------------

    internal const ushort MsgTagLevelUp = 58057; // 0xE2C9
    internal const int LevelUpBodyLen = 40;

    /// <summary>
    /// Builds RMI 2394 OnLevelUp. 40 bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The actor handle sits at <b>blob+4</b>, not blob+8, and carries the 0x11 PC tag. Its own sibling 2395
    /// does the opposite.
    /// </para>
    /// <para>
    /// The experience field is progress WITHIN the new level, which is exactly what the server
    /// already tracks after subtracting the level's requirement.
    /// </para>
    /// <para>
    /// cur/max direction cannot be settled inside this message — the pairs are equal — so it follows 2401,
    /// whose consumer feeds the same player fields and where <c>cur &lt;= max</c> holds.
    /// </para>
    /// <para>
    /// SP is clamped to 16 bits because the client stores only the low half; a larger value would
    /// silently truncate. Length is load-bearing: the client's reader does no bounds check and
    /// resizes the ByteArray to the DECLARED length first, so a short blob is a heap over-read.
    /// </para>
    /// </remarks>
    public static byte[] BuildLevelUp2394(uint actorHandle, byte level, ulong expWithinLevel,
        uint maxHp, uint curHp, uint maxSp, uint curSp, ushort statPointsGranted)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagLevelUp);                          // blob+2
            p.WriteUInt32(actorHandle);                            // blob+4  handle, NOT zero
            p.WriteByte(level);                                    // blob+8
            p.WriteUInt64(expWithinLevel);                         // blob+9
            p.WriteUInt32(maxHp);                                  // blob+17
            p.WriteUInt32(curHp);                                  // blob+21
            p.WriteUInt32(Math.Min(maxSp, ushort.MaxValue));       // blob+25 client truncates to u16
            p.WriteUInt32(Math.Min(curSp, ushort.MaxValue));       // blob+29
            p.WriteByte(0);                                        // blob+33 unmodelled, always 0
            p.WriteUInt16(statPointsGranted);                      // blob+34
            p.WriteByte(0);                                        // blob+36 jobMasteryPoint
            p.WriteByte(0);                                        // blob+37 jobMasteryLevel
        });
        return pw.ToArray();
    }

    /// <summary>Highest level that grants 1 stat point per level-up; levels above it grant 2.</summary>
    private const int LastSingleStatPointLevel = 24;

    /// <summary>
    /// Stat points granted for a level transition: 1 per level up to 24, 2 above.
    /// </summary>
    /// <remarks>
    /// Wire-only: the client never reads this slot. The rule matches observed level-up frames; the low-side
    /// branch rests on few single-level samples plus the 1→18 jump, which carries exactly 17 = 17 levels × 1.
    /// </remarks>
    public static ushort StatPointsForLevelUp(int fromLevel, int toLevel)
    {
        var total = 0;
        for (var l = fromLevel + 1; l <= toLevel; l++)
            total += l <= LastSingleStatPointLevel ? 1 : 2;
        return (ushort)Math.Clamp(total, 0, ushort.MaxValue);
    }

    // ---- 2395 OnLevelUpNotify (broadcast) --------------------------------------------------

    internal const ushort MsgTagLevelUpNotify = 58058; // 0xE2CA
    internal const int LevelUpNotifyBodyLen = 17;

    /// <summary>
    /// Builds RMI 2395 OnLevelUpNotify — the broadcast half of a level up.
    /// </summary>
    /// <remarks>
    /// The handle is at blob+8 here, unlike its sibling 2394 which puts it at blob+4. Level is a u8 read; a u16
    /// read would report impossible levels. The level is strictly increasing along a character's chain.
    /// </remarks>
    public static byte[] BuildLevelUpNotify2395(uint actorHandle, byte level)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagLevelUpNotify); // blob+2
            p.WriteUInt32(0);                   // blob+4  never read
            p.WriteUInt32(actorHandle);         // blob+8  handle HERE, not at +4
            p.WriteByte(level);                 // blob+12
            p.WriteByte(0);                     // blob+13 jobMasteryPoint
            p.WriteByte(0);                     // blob+14 jobMasteryLevel
        });
        return pw.ToArray();
    }

    // ---- 2329 OnUseItem --------------------------------------------------------------------

    internal const ushort MsgTagUseItem = 58022; // 0xE2A6
    internal const int UseItemBodyLen = 24;

    /// <summary>2329 blob+21 useSource: the item was consumed by an item skill (SkillStartItemHandler).</summary>
    internal const byte UseSourceItemSkill = 0;

    /// <summary>2329 blob+21 useSource: answers a 1323 ReqPCItemUse (ItemUseHandler); the common value.</summary>
    internal const byte UseSourceItemUse = 1;

    /// <summary>2329 blob+21 useSource: the paying item of a guild rename (GuildRenameHandler).</summary>
    internal const byte UseSourceGuildRename = 3;

    /// <summary>
    /// Builds RMI 2329 OnUseItem. 24 bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>remainingCount</c> is the stack size AFTER the use, and 0 removes the item. Across successive uses of
    /// the same item the value decrements by exactly 1.
    /// </para>
    /// <para>
    /// The (slot, bag) partition follows the client's router arithmetic: bag 1 uses slots 50..69, bags 2-4 use
    /// 0..34.
    /// </para>
    /// <para>
    /// <c>useSource</c> depends on the path that consumed the item, so each caller passes the value
    /// for its own path. 1 answers a 1323 ReqPCItemUse (ItemUseHandler). 3 is the paying item of a guild rename:
    /// each successful 1586 ReqRenameGuildName (2855 = 0) is answered by a 2329 carrying 3, and a refused one
    /// (2855 = 44) by none (GuildRenameHandler). 0 is an item skill (SkillStartItemHandler). A value 2 also
    /// exists, but the path behind it is not identified, so no caller sends it.
    /// </para>
    /// </remarks>
    public static byte[] BuildUseItem2329(uint actorHandle, byte slotIndex, byte bagId,
        ulong itemUid, byte remainingCount, byte useSource = UseSourceItemUse, short resultCode = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUseItem);  // blob+2
            p.WriteUInt32(actorHandle);    // blob+4
            p.WriteInt16(resultCode);      // blob+8  SIGNED; non-zero aborts the client handler
            p.WriteByte(slotIndex);        // blob+10
            p.WriteByte(bagId);            // blob+11 1..4, never 0
            p.WriteUInt64(itemUid);        // blob+12 one u64 -- echo the request's uid whole
            p.WriteByte(remainingCount);   // blob+20 count AFTER the use
            p.WriteByte(useSource);        // blob+21
        });
        return pw.ToArray();
    }

    // ---- 2471 OnLootingAuthority -----------------------------------------------------------

    internal const ushort MsgTagLootingAuthority = 58334; // 0xE3DE
    internal const int LootingAuthorityBodyLen = 17;      // one entry: 12 + 5*1

    /// <summary>2471 element+4 flag: the actor is added to the loot list.</summary>
    private const byte LootGranted = 1;

    /// <summary>2471 element+4 flag: the actor is removed from the loot list.</summary>
    private const byte LootRevoked = 0;

    /// <summary>
    /// One loot-list edit: an actor handle and whether the receiving client may loot it.
    /// </summary>
    /// <remarks>
    /// <c>Granted</c> is a single byte on the wire, and only 0 and 1 occur. It is exposed as a bool so no third
    /// value can be invented; the client stores the byte verbatim in the list node and only ever tests it for
    /// non-zero.
    /// </remarks>
    public readonly record struct LootAuthorityEntry(uint ActorHandle, bool Granted);

    /// <summary>
    /// Builds RMI 2471 OnLootingAuthority — the message that puts a corpse in the receiving
    /// player's loot list, granting (or withdrawing) the right to loot it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always S2C, unencrypted, and <b>17 bytes — a single body length</b>. Layout:
    /// </para>
    /// <code>
    ///   [compactLen 01 0f][u16 selfLen = 15]     blob+0   15 == len(blob)
    ///   [u16 msgTag = 0xE3DE]                    blob+2   constant
    ///   [u32 0]                                  blob+4   zero
    ///   [u16 count]                              blob+8   1 in practice
    ///   [u32 actorHandle][u8 granted] * count    blob+10  5-byte elements
    /// </code>
    /// <para>
    /// The 5-byte element and the u16 count are read off the client's handler: it opens a nested reader over the
    /// blob, consumes 8 bytes it discards (that is why blob+4 is unused here), then reads a <c>u16</c> count and
    /// <c>count</c> × 5 bytes, and walks the result with a literal <c>(end − begin) / 5</c> stride reading a
    /// <c>u32</c> at +0 and a <c>u8</c> at +4. A non-zero byte adds the actor to the loot list, which lights the
    /// corpse's loot indicator; a zero byte removes it. So <c>len(blob) == 10 + 5*count</c>.
    /// </para>
    /// <para>
    /// ⚠️ The count at blob+8 is one of the length reads the client actually TESTS: a body that ends before this
    /// u16 makes the client abandon the whole structure.
    /// </para>
    /// <para>
    /// The handle carries the 0x13 NPC actor tag and is never zero, which identifies it as the corpse rather
    /// than the killer: it matches the dead-actor slot of 2392 OnDeadNotify (blob+8), not the killer slot
    /// (blob+12). Relation to 2392:
    /// </para>
    /// <list type="bullet">
    /// <item>the grant PRECEDES the death notify, so it is emitted before the death is announced, not after;</item>
    /// <item>a revoke follows its own grant, never the other way round: grant-then-revoke is the
    /// lifecycle.</item>
    /// </list>
    /// <para>
    /// Not every death comes with a grant: a death that is another player's kill arrives with NO grant for that
    /// corpse, which is not what a broadcast to everyone who sees the death would look like.
    /// </para>
    /// <para>
    /// ⚠️ The original sends <c>count == 1</c> in every frame, so the multi-entry
    /// form is attested by the client's reader loop and NOT by the wire. Prefer the single-entry
    /// overload.
    /// </para>
    /// </remarks>
    public static byte[] BuildLootingAuthority2471(IReadOnlyList<LootAuthorityEntry> entries)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagLootingAuthority);        // blob+2
            p.WriteUInt32(0);                             // blob+4  read into a discarded prologue
            p.WriteUInt16((ushort)entries.Count);         // blob+8  TESTED read, see remarks
            foreach (var e in entries)
            {
                p.WriteUInt32(e.ActorHandle);             // element+0
                p.WriteByte(e.Granted ? LootGranted : LootRevoked); // element+4
            }
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2471 OnLootingAuthority for a single corpse — the shape sent in practice.
    /// </summary>
    /// <param name="corpseHandle">
    /// The dead actor's handle. Its low byte is the 0x13 NPC type tag.
    /// </param>
    /// <param name="granted">
    /// True adds the corpse to the receiver's loot list and lights the loot indicator; false
    /// removes it.
    /// </param>
    public static byte[] BuildLootingAuthority2471(uint corpseHandle, bool granted = true)
        => BuildLootingAuthority2471(new[] { new LootAuthorityEntry(corpseHandle, granted) });

    // ---- 2396 OnSocialAni ------------------------------------------------------------------

    internal const ushort MsgTagSocialAni = 58061; // 0xE2CD
    internal const int SocialAniBodyLen = 18;

    /// <summary>
    /// Builds RMI 2396 OnSocialAni — an emote. 18 bytes.
    /// </summary>
    /// <remarks>
    /// The handle appears at BOTH blob+4 and blob+8, identical. The client only
    /// reads +8 and +12, but writing the mirror keeps the bytes identical to the original. Broadcast, so a
    /// malformed body takes out every client in range rather than one.
    /// </remarks>
    public static byte[] BuildSocialAni2396(uint actorHandle, uint socialActionId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSocialAni); // blob+2
            p.WriteUInt32(actorHandle);     // blob+4  mirror
            p.WriteUInt32(actorHandle);     // blob+8  the one the client reads
            p.WriteUInt32(socialActionId);  // blob+12
        });
        return pw.ToArray();
    }
}
