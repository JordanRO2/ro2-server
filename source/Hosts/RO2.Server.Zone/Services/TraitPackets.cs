using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.Worlds.Domain;
using RO2.Characters.Domain;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the trait family: the stat sheet (2498 OnPcTrait), trait modifiers (2499 OnPcTraitChange),
/// movement speed (2497 OnChangeActorMoveSpeed) and the job-mastery point counter (2986 NfyJobMasteryPointChange).
/// </summary>
/// <remarks>
/// 2497, 2498 and 2499 carry a ByteArray blob behind the compact-length envelope; the blob starts with a u16 selfLen
/// (its own length, counting itself), a u16 msgTag and a u32 that is always zero. 2986 uses the u32 envelope instead
/// (a leading u32 blob length, no compact marker). Each opcode has exactly one fixed body length.
/// </remarks>
public static class TraitPackets
{
    /// <summary>Blob offset of the u16 selfLen shared by 2497, 2498 and 2499.</summary>
    internal const int BlobSelfLenOffset = 0;

    /// <summary>Blob offset of the u16 msgTag shared by 2497, 2498 and 2499.</summary>
    internal const int BlobMsgTagOffset = 2;

    /// <summary>Blob offset of the u32 shared by 2497, 2498 and 2499 that is always zero.</summary>
    internal const int BlobReservedOffset = 4;

    /// <summary>Value of every reserved field in this family: the wire carries zero there.</summary>
    internal const uint Reserved = 0;

    /// <summary>Width in bytes of one u16 stat slot in the 2498 blob.</summary>
    internal const int StatSlotSize = sizeof(ushort);

    // ================= 2497 OnChangeActorMoveSpeed =========================================

    /// <summary>msgTag of 2497 (58364).</summary>
    internal const ushort MsgTagChangeActorMoveSpeed = 0xE3FC;

    /// <summary>Body length of 2497.</summary>
    internal const int ChangeActorMoveSpeedBodyLen = 18;

    /// <summary>
    /// Movement speed sent at world entry when no class or mount speed source exists. Defaults to 6.0, the most common run speed.
    /// </summary>
    internal const float PcModalWorldEntrySpeed = ActorMoveSpeedRules.PcModalWorldEntrySpeed;

    /// <summary>
    /// Builds RMI 2497 OnChangeActorMoveSpeed, which sets an actor's movement speed.
    /// </summary>
    /// <remarks>
    /// Layout: [0..1] compact marker and length; [2..3] selfLen 16; [4..5] msgTag; [6..9] u32 0; [10..13] actor handle; [14..17] f32 move speed.
    /// The speed must be finite and strictly greater than zero, because zero or less freezes the actor on the client.
    /// Broadcast to the whole map without exclusion, so the actor receives its own copy.
    /// Sent on world entry, on NPC spawn, and after any buff, debuff, mount or stance change that alters speed.
    /// </remarks>
    /// <param name="actorHandle">Type-tagged actor handle: low byte 0x11 for a player, 0x13 for an NPC.</param>
    /// <param name="moveSpeed">Movement speed. Must be finite and strictly greater than zero.</param>
    public static byte[] BuildActorMoveSpeed2497(uint actorHandle, float moveSpeed)
    {
        if (!float.IsFinite(moveSpeed) || moveSpeed <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveSpeed), moveSpeed,
                "2497 with a speed of zero or less (or NaN) freezes the actor on the client for 100,000 seconds; " +
                "only a later positive 2497 clears it.");
        }

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagChangeActorMoveSpeed); // blob+2   msgTag
            p.WriteUInt32(Reserved);                   // blob+4   always zero
            p.WriteUInt32(actorHandle);                // blob+8   actor the speed applies to
            p.WriteSingle(moveSpeed);                  // blob+12  movement speed
        });
        return pw.ToArray();
    }

    // ================= 2498 OnPcTrait ======================================================

    /// <summary>msgTag of 2498 (58316).</summary>
    internal const ushort MsgTagPcTrait = 0xE3CC;

    /// <summary>
    /// Length of the 2498 blob. The client copies exactly this many bytes, so the length is required.
    /// </summary>
    internal const int PcTraitBlobLen = 140;

    /// <summary>Body length of 2498: a 3-byte compact length (marker 2, u16 140) plus the 140-byte blob.</summary>
    internal const int PcTraitBodyLen = 143;

    /// <summary>Blob offset of max HP (u32).</summary>
    internal const int PcTraitMaxHpOffset = 8;

    /// <summary>Blob offset of max SP (u16).</summary>
    internal const int PcTraitMaxSpOffset = 12;

    /// <summary>Blob offset of current HP (u32).</summary>
    internal const int PcTraitCurHpOffset = 72;

    /// <summary>Blob offset of current SP (u16).</summary>
    internal const int PcTraitCurSpOffset = 76;

    /// <summary>Blob offset of an undetermined f32 field.</summary>
    internal const int PcTraitScale68Offset = 68;

    /// <summary>
    /// Blob offset of the HP regeneration amount (u16).
    /// </summary>
    internal const int PcTraitHpRegenOffset = 78;

    /// <summary>
    /// Blob offset of the SP regeneration amount (u16).
    /// </summary>
    internal const int PcTraitSpRegenOffset = 80;

    /// <summary>Number of primary stats stored from <see cref="PcTraitPrimaryStatsOffset"/>.</summary>
    internal const int PcTraitPrimaryStatCount = 6;

    /// <summary>
    /// Blob offset of the first of the six primary stats (u16 each), in the order STR, AGI, INT, WIS, VIT, LUK.
    /// </summary>
    internal const int PcTraitPrimaryStatsOffset = 14;

    /// <summary>Number of allocated bonus primaries stored from <see cref="PcTraitBonusStatsOffset"/>.</summary>
    internal const int PcTraitBonusStatCount = 5;

    /// <summary>
    /// Blob offset of the unspent bonus stat point pool (u16), shown as points to assign on the character sheet.
    /// </summary>
    internal const int PcTraitStatPointsOffset = 38;

    /// <summary>
    /// Blob offset of the five allocated bonus primaries (u16 each), in the order STR, AGI, INT, WIS, VIT.
    /// The effective primary is the base plus the bonus.
    /// </summary>
    internal const int PcTraitBonusStatsOffset = 26;

    /// <summary>
    /// Derived combat stats in the blob, keyed by trait id, with their blob offsets (u16 each).
    /// </summary>
    /// <remarks>
    /// Trait ids: 23 physical attack, 25 magic attack, 26 defense, 31 attack speed slot (carries the acceleration
    /// rate), 58 to 63 precision, evade, critical, guard, vitality and acceleration levels, 71 and 72 PvP damage
    /// increase and reduction rates (percent), 94 penetration, 98 casting speed.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<byte, int> DerivedStatBlobOffsets = new Dictionary<byte, int>
    {
        [23] = 40, [25] = 44, [26] = 50, [31] = 86,
        [58] = 88, [59] = 90, [60] = 92, [61] = 94, [62] = 96, [63] = 98,
        [71] = 100, [72] = 102,
        [94] = 128, [98] = 132,
    };

    /// <summary>
    /// Modal value of the f32 at blob+68: 3.0.
    /// </summary>
    internal const float PcTraitScale68Modal = 3.0f;

    /// <summary>
    /// Builds RMI 2498 OnPcTrait, the local character's stat sheet.
    /// </summary>
    /// <remarks>
    /// Layout: [0..2] compact marker and length; [3..4] selfLen 140; [5..6] msgTag; [7..10] u32 0; [11..14] u32 maxHp;
    /// [15..16] u16 maxSp; [17..70] primaries and bonus values; [71..74] f32; [75..78] u32 curHp; [79..80] u16 curSp;
    /// [81..82] u16 hpRegen; [83..84] u16 spRegen; [85..138] derived trait values and enforce-card stats.
    /// The sheet is unicast to the owning session and carries no actor identity.
    /// It is sent in full after any change that could affect a stat, typically after a run of trait modifier lines.
    /// Fields not derived here are sent as zero unless a template is supplied.
    /// </remarks>
    /// <param name="maxHp">Maximum HP.</param>
    /// <param name="maxSp">Maximum SP.</param>
    /// <param name="curHp">Current HP.</param>
    /// <param name="curSp">Current SP.</param>
    /// <param name="scaleAtBlob68">Value of the f32 field at blob+68. Defaults to 3.0.</param>
    /// <param name="traitTemplate">Optional 140-byte blob supplying the bytes not derived here. Leave unset unless the template is known to be correct.</param>
    public static byte[] BuildPcTrait2498(
        uint maxHp, ushort maxSp, uint curHp, ushort curSp,
        float scaleAtBlob68 = PcTraitScale68Modal,
        ReadOnlySpan<byte> traitTemplate = default,
        IReadOnlyDictionary<byte, int>? derivedByStatId = null,
        IReadOnlyList<ushort>? primaryStats6 = null,
        ushort bonusStatPoints = 0,
        IReadOnlyList<ushort>? bonusStats5 = null,
        ushort? hpRegen78 = null,
        ushort? spRegen80 = null)
    {
        if (!traitTemplate.IsEmpty && traitTemplate.Length != PcTraitBlobLen)
        {
            throw new ArgumentException(
                $"The 2498 trait template must be exactly {PcTraitBlobLen} bytes: the client " +
                "copies that many bytes out of the blob with no length check.",
                nameof(traitTemplate));
        }

        // The sheet never carries current HP above max HP, nor current SP above a non-zero max SP. A caller that
        // swapped the two arguments would otherwise send a sheet the client accepts silently, so reject it here.
        if (curHp > maxHp)
        {
            throw new ArgumentOutOfRangeException(
                nameof(curHp), curHp,
                $"2498 carries curHp <= maxHp; got curHp={curHp} against maxHp={maxHp}. " +
                "Check the argument order: max HP goes to blob+8 and current HP to blob+72.");
        }

        if (maxSp != 0 && curSp > maxSp)
        {
            throw new ArgumentOutOfRangeException(
                nameof(curSp), curSp,
                $"2498 carries curSp <= maxSp when max SP is non-zero; got curSp={curSp} against maxSp={maxSp}.");
        }

        var blob = new byte[PcTraitBlobLen];
        if (!traitTemplate.IsEmpty)
            traitTemplate.CopyTo(blob);

        var b = blob.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(b[BlobSelfLenOffset..], PcTraitBlobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b[BlobMsgTagOffset..], MsgTagPcTrait);
        BinaryPrimitives.WriteUInt32LittleEndian(b[BlobReservedOffset..], Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(b[PcTraitMaxHpOffset..], maxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(b[PcTraitMaxSpOffset..], maxSp);
        BinaryPrimitives.WriteSingleLittleEndian(b[PcTraitScale68Offset..], scaleAtBlob68);
        BinaryPrimitives.WriteUInt32LittleEndian(b[PcTraitCurHpOffset..], curHp);
        BinaryPrimitives.WriteUInt16LittleEndian(b[PcTraitCurSpOffset..], curSp);

        // Unspent bonus stat point pool, written over the template value.
        BinaryPrimitives.WriteUInt16LittleEndian(b[PcTraitStatPointsOffset..], bonusStatPoints);

        // Derived combat stats: one u16 at each offset of DerivedStatBlobOffsets. Stat ids not listed there are
        // ignored, so a caller may pass a superset; a zero value is written as zero.
        if (derivedByStatId is not null)
        {
            foreach (var (statId, off) in DerivedStatBlobOffsets)
            {
                if (derivedByStatId.TryGetValue(statId, out var v))
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        b[off..], (ushort)Math.Clamp(v, 0, ushort.MaxValue));
            }
        }

        // Primary stats: PcTraitPrimaryStatCount consecutive u16 from PcTraitPrimaryStatsOffset.
        if (primaryStats6 is not null)
        {
            int n = Math.Min(PcTraitPrimaryStatCount, primaryStats6.Count);
            for (int i = 0; i < n; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    b[(PcTraitPrimaryStatsOffset + StatSlotSize * i)..], primaryStats6[i]);
        }

        // Allocated bonus stats: PcTraitBonusStatCount consecutive u16 from PcTraitBonusStatsOffset.
        if (bonusStats5 is not null)
        {
            int n = Math.Min(PcTraitBonusStatCount, bonusStats5.Count);
            for (int i = 0; i < n; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    b[(PcTraitBonusStatsOffset + StatSlotSize * i)..], bonusStats5[i]);
        }

        // HP and SP regeneration amounts. Null keeps the template's bytes.
        if (hpRegen78 is { } hpRegen)
            BinaryPrimitives.WriteUInt16LittleEndian(b[PcTraitHpRegenOffset..], hpRegen);
        if (spRegen80 is { } spRegen)
            BinaryPrimitives.WriteUInt16LittleEndian(b[PcTraitSpRegenOffset..], spRegen);

        using var pw = new PacketWriter();
        pw.WriteCompactLength(PcTraitBlobLen); // 140 >= 0x80, so the envelope is [marker 2][u16 140]
        pw.WriteBytes(blob);
        return pw.ToArray();
    }

    // ================= 2499 OnPcTraitChange =================================================

    /// <summary>msgTag of 2499 (58365).</summary>
    internal const ushort MsgTagPcTraitChange = 0xE3FD;

    /// <summary>Body length of 2499.</summary>
    internal const int PcTraitChangeBodyLen = 27;

    /// <summary>Trait kind of a flat modifier: the value is added to the stat as is.</summary>
    internal const uint TraitKindFlat = 3;

    /// <summary>Trait kind of a percent modifier: the value is in units of 1/10,000.</summary>
    internal const uint TraitKindPercent = 4;

    /// <summary>
    /// The u8 at blob+24. Always 1.
    /// </summary>
    internal const byte PcTraitChangeFlagConstant = 1;

    /// <summary>
    /// Builds RMI 2499 OnPcTraitChange: one trait modifier line granted or refreshed by an effect.
    /// </summary>
    /// <remarks>
    /// Layout: [0] marker; [1..2] selfLen 25; [4..5] msgTag; [6..9] u32 0; [10..13] u32 traitKind; [14..17] u32 traitId;
    /// [18..21] f32 value; [22..25] u32 effectId; [26] u8 1.
    /// The client keeps the lines in a vector keyed by (traitKind, traitId, effectId) and updates the value on refresh.
    /// A line does not change the sheet by itself: the closing 2498 carries the recomputed values.
    /// The client drops an effect's lines when the effect is removed (2494), so no message is needed to remove them.
    /// Send one 2499 per trait line of an effect, then one 2498.
    /// </remarks>
    /// <param name="traitKind">blob+8. <see cref="TraitKindFlat"/> adds a flat amount; <see cref="TraitKindPercent"/> is a percent in 1/10,000.</param>
    /// <param name="traitId">blob+12. The trait id.</param>
    /// <param name="value">blob+16, f32. The modifier value.</param>
    /// <param name="effectId">blob+20. Must be an id present in Effect_Table.</param>
    /// <param name="flag">blob+24. Always 1; leave the default.</param>
    public static byte[] BuildPcTraitChange2499(
        uint traitKind, uint traitId, float value, uint effectId,
        byte flag = PcTraitChangeFlagConstant)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagPcTraitChange); // blob+2   msgTag
            p.WriteUInt32(Reserved);            // blob+4   always zero
            p.WriteUInt32(traitKind);           // blob+8   identity key: flat or percent
            p.WriteUInt32(traitId);             // blob+12  identity key: the trait
            p.WriteSingle(value);               // blob+16  the modifier value (mutable)
            p.WriteUInt32(effectId);            // blob+20  identity key: the source effect
            p.WriteByte(flag);                  // blob+24  flag (mutable), always 1
        });
        return pw.ToArray();
    }

    // ================= 2986 NfyJobMasteryPointChange ========================================

    /// <summary>
    /// Length of the 2986 blob, carried as a leading u32 (16).
    /// </summary>
    internal const uint JobMasteryBlobLen = 16;

    /// <summary>Body length of 2986 (20 bytes): the 4-byte u32 length plus the 16-byte blob.</summary>
    internal const int JobMasteryBodyLen = 20;

    /// <summary>
    /// Builds RMI 2986 NfyJobMasteryPointChange, which updates the job-mastery point counter and shows a gain or loss message.
    /// Uses a U32 envelope, not a compact marker.
    /// </summary>
    /// <remarks>
    /// Layout: [0..3] u32 16; [4..11] i64 delta; [12..15] u32 total; [16..19] u32 0.
    /// A nonzero delta shows a gain or loss message; a zero delta shows none.
    /// Sent unicast before loot authority on a kill, and once at world entry for a character that has points.
    /// </remarks>
    /// <param name="delta">Change in points. A negative value shows the loss message.</param>
    /// <param name="totalPoints">The counter after the change.</param>
    public static byte[] BuildJobMasteryPointChange2986(long delta, uint totalPoints)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(JobMasteryBlobLen); // body+0   u32 envelope: blob length
        pw.WriteInt64(delta);              // blob+0    signed change
        pw.WriteUInt32(totalPoints);       // blob+8    counter after the change
        pw.WriteUInt32(Reserved);          // blob+12   always zero
        return pw.ToArray();
    }

    // ================= trait builders (moved from WorldEntryService, B5 Phase-0 S3) ============

 /// <summary>Number of decimals the class regeneration coefficients are rounded to before they are multiplied.</summary>
    private const int RegenCoefficientDecimals = 6;

 /// <summary>Trait ids of the primary stats, in the order of their slots.</summary>
    private const byte TraitStr = 0, TraitAgi = 1, TraitInt = 2, TraitWis = 3, TraitVit = 4, TraitLuk = 5;
 /// <summary>Trait id of the flat maximum HP bonus.</summary>
    private const byte TraitMaxHp = 21;
 /// <summary>Trait id of the flat maximum SP bonus.</summary>
    private const byte TraitMaxSp = 22;
 /// <summary>Trait id of the flat HP regeneration bonus.</summary>
    private const byte TraitHpRestore = 28;
 /// <summary>Trait id of the flat SP regeneration bonus.</summary>
    private const byte TraitSpRestore = 29;
 /// <summary>Trait id of the auto-attack range of the equipped weapon.</summary>
    private const byte TraitAutoAttackRange = 42;
 /// <summary>The auto-attack reach sent in the trait message is this base plus the weapon's auto-attack range.</summary>
    private const float AutoAttackReachBase = 1f;

 /// <summary>Maximum HP estimate when no class stat row exists: this base plus <see cref="FallbackMaxHpPerLevel"/> per level.</summary>
    private const int FallbackMaxHpBase = 100;
 /// <summary>Maximum HP added per level in the no-class-row estimate.</summary>
    private const int FallbackMaxHpPerLevel = 50;
 /// <summary>Maximum SP estimate when no class stat row exists: this base plus <see cref="FallbackMaxSpPerLevel"/> per level.</summary>
    private const int FallbackMaxSpBase = 50;
 /// <summary>Maximum SP added per level in the no-class-row estimate.</summary>
    private const int FallbackMaxSpPerLevel = 20;

 /// <summary>Length of the compact length prefix in front of the 140-byte trait blob: the marker byte 2 and a u16 length.</summary>
    private const int PcTraitPrefixLen = TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen;
 /// <summary>Value of each primary stat slot of the trait initialization message before the allocated bonus is added.</summary>
    private const int PcInitPrimaryStatBase = 1;
 /// <summary>Value of the f32 at blob offset 68 in the trait initialization message.</summary>
    private const float PcInitTraitScale68 = 1.0f;

 /// <summary>
 /// Donor body of the 2498 trait message: a 143-byte frame whose stat fields are replaced with the character's own values.
 /// </summary>
 /// <remarks>
 /// Layout, as byte offsets into the frame: 0 to 2 is the compact length prefix (marker 2, u16 140, see <see cref="PcTraitPrefixLen"/>).
 /// The 140-byte blob starts at 3 with a u16 self length and the u16 message tag <see cref="TraitPackets.MsgTagPcTrait"/>.
 /// Within the blob: u32 maximum HP at 8, u16 maximum SP at 12, six u16 primary stats from 14, f32 at 68 (the weapon's auto-attack reach),
 /// u32 current HP at 72, u16 current SP at 76 and the u16 HP and SP regeneration amounts at 78 and 80 (see the <c>TraitPackets.PcTrait*Offset</c> constants).
 /// The donor holds a level-1 character's values: 224 HP, 15 SP, every primary stat 1 except LUK 0, and reach 1.0.
 /// Bytes that no builder overlays keep the donor's values.
 /// </remarks>
    internal static readonly byte[] PcTrait2498Donor = BuildPcTrait2498Default();

 /// <summary>Maximum HP of the donor, a level-1 character's value.</summary>
    private const uint DonorMaxHp = 224;
 /// <summary>Maximum SP of the donor.</summary>
    private const ushort DonorMaxSp = 15;
 /// <summary>Value of each of the donor's STR, AGI, INT, WIS and VIT.</summary>
    private const ushort DonorPrimaryStat = 1;
 /// <summary>Value of the donor's LUK.</summary>
    private const ushort DonorLuk = 0;
 /// <summary>Physical attack (trait 23) of the donor.</summary>
    private const ushort DonorPhysicalAttack = 2;
 /// <summary>Value of the u16 at blob offset 54, whose meaning is undetermined; the donor keeps 1.</summary>
    private const ushort DonorUnnamedWord54 = 1;
 /// <summary>HP regeneration amount of the donor.</summary>
    private const ushort DonorHpRegen = 2;
 /// <summary>SP regeneration amount of the donor.</summary>
    private const ushort DonorSpRegen = 1;
 /// <summary>Precision (trait 58) of the donor.</summary>
    private const ushort DonorPrecision = 4;
 /// <summary>Evade (trait 59) of the donor.</summary>
    private const ushort DonorEvade = 2;
 /// <summary>Critical (trait 60) of the donor.</summary>
    private const ushort DonorCritical = 1;
 /// <summary>Guard (trait 61) of the donor.</summary>
    private const ushort DonorGuard = 1;

 /// <summary>Blob offset of the physical attack u16 (trait 23).</summary>
    private const int PcTraitPhysicalAttackOffset = 40;
 /// <summary>Blob offset of the u16 whose meaning is undetermined.</summary>
    private const int PcTraitUnnamedWord54Offset = 54;
 /// <summary>Blob offset of the precision u16 (trait 58). Evade, critical and guard (traits 59 to 61) follow at two-byte steps.</summary>
    private const int PcTraitPrecisionOffset = 88;

 /// <summary>
 /// Writes the default bytes of <see cref="PcTrait2498Donor"/> field by field. Every blob field not written here is zero.
 /// </summary>
    private static byte[] BuildPcTrait2498Default()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            // The payload starts at blob offset 2, right after the u16 self length. GoTo zero-fills up to a blob offset.
            void GoTo(int blobOffset) => p.WriteZeros(blobOffset - TraitPackets.BlobMsgTagOffset - p.Length);

            p.WriteUInt16(TraitPackets.MsgTagPcTrait);                    // blob+2    msgTag
            p.WriteUInt32(TraitPackets.Reserved);                         // blob+4    always zero
            p.WriteUInt32(DonorMaxHp);                                    // blob+8    max HP
            p.WriteUInt16(DonorMaxSp);                                    // blob+12   max SP

            // blob+14..25: STR, AGI, INT, WIS, VIT, LUK.
            for (int i = 0; i < TraitPackets.PcTraitPrimaryStatCount - 1; i++)
                p.WriteUInt16(DonorPrimaryStat);
            p.WriteUInt16(DonorLuk);

            // blob+26..35: allocated bonus STR, AGI, INT, WIS, VIT, all zero.
            for (int i = 0; i < TraitPackets.PcTraitBonusStatCount; i++)
                p.WriteUInt16(0);

            GoTo(TraitPackets.PcTraitStatPointsOffset);
            p.WriteUInt16(0);                                             // blob+38   unspent bonus stat points

            GoTo(PcTraitPhysicalAttackOffset);
            p.WriteUInt16(DonorPhysicalAttack);                           // blob+40   physical attack

            GoTo(PcTraitUnnamedWord54Offset);
            p.WriteUInt16(DonorUnnamedWord54);                            // blob+54   undetermined

            GoTo(TraitPackets.PcTraitScale68Offset);
            p.WriteSingle(AutoAttackReachBase);                           // blob+68   auto-attack reach
            p.WriteUInt32(DonorMaxHp);                                    // blob+72   current HP
            p.WriteUInt16(DonorMaxSp);                                    // blob+76   current SP
            p.WriteUInt16(DonorHpRegen);                                  // blob+78   HP regeneration
            p.WriteUInt16(DonorSpRegen);                                  // blob+80   SP regeneration

            GoTo(PcTraitPrecisionOffset);
            p.WriteUInt16(DonorPrecision);                                // blob+88   precision
            p.WriteUInt16(DonorEvade);                                    // blob+90   evade
            p.WriteUInt16(DonorCritical);                                 // blob+92   critical
            p.WriteUInt16(DonorGuard);                                    // blob+94   guard

            GoTo(TraitPackets.PcTraitBlobLen);                            // blob+96..139 zero (vitality, acceleration, PvP rates, penetration, casting speed, ...)
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2498 trait message for a character already in the world, with its gear-based stats and current HP and SP.
 /// </summary>
 /// <remarks>
 /// Current HP and SP come from the live player state, not the login snapshot, so a re-sent message does not reset them.
 /// Current values are clamped to the computed maximums.
 /// Callers that include active effects go through the effect service instead of calling this directly.
 /// </remarks>
    internal static byte[] BuildFullPcTrait2498(
        Character character, GameDataStore? gameData, IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? equipped,
        int currentHp, int currentSp)
    {
        EquipStats? derived = gameData is not null && equipped is not null
            ? StatService.ComputeEquipmentStats(gameData, equipped)
            : null;
        return BuildPcTrait2498FromDonor(PcTrait2498Donor, character, derived, gameData, (currentHp, currentSp));
    }

 /// <summary>
 /// The current and maximum HP and SP carried by a 143-byte trait message, or null when the body has another length.
 /// </summary>
    internal static (uint CurHp, ushort CurSp, uint MaxHp, ushort MaxSp)? VitalsOf(ReadOnlySpan<byte> pcTrait2498)
    {
        if (pcTrait2498.Length != TraitPackets.PcTraitBodyLen) return null;
        var blob = pcTrait2498[PcTraitPrefixLen..];   
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitCurHpOffset..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitCurSpOffset..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitMaxHpOffset..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitMaxSpOffset..]));
    }

 /// <summary>Message tag of the trait initialization message.</summary>
    private const ushort MsgTagPcInitTrait = 0xE396; 

 /// <summary>
 /// Builds the 143-byte trait initialization message sent before a trait message at world entry and after a stat allocation.
 /// </summary>
 /// <remarks>
 /// Only the primary stats, the bonus fields and the current HP and SP are filled in. All other bytes are zero.
 /// </remarks>
 /// <param name="followingPcTrait2498">The trait message sent right after this one. Its current HP and SP are copied here. A body of another length leaves them zero.</param>
    internal static byte[] BuildPcInitTrait2595(Character character, ReadOnlySpan<byte> followingPcTrait2498)
    {
        var blob = new byte[TraitPackets.PcTraitBlobLen];
        var b = blob.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, TraitPackets.PcTraitBlobLen); 
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b[2..], MsgTagPcInitTrait);      
        
        int[] bonus5 = { character.BonusStr, character.BonusAgi, character.BonusInt, character.BonusWis, character.BonusVit };
        for (int i = 0; i < bonus5.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                b[(TraitPackets.PcTraitPrimaryStatsOffset + 2 * i)..],
                (ushort)Math.Min(PcInitPrimaryStatBase + Math.Clamp(bonus5[i], 0, ushort.MaxValue), ushort.MaxValue));
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b[TraitPackets.PcTraitScale68Offset..], PcInitTraitScale68);
        if (followingPcTrait2498.Length == TraitPackets.PcTraitBodyLen)
        {
            var trait = followingPcTrait2498[PcTraitPrefixLen..];   
            trait.Slice(TraitPackets.PcTraitCurHpOffset, sizeof(uint)).CopyTo(b[TraitPackets.PcTraitCurHpOffset..]);
            trait.Slice(TraitPackets.PcTraitCurSpOffset, sizeof(ushort)).CopyTo(b[TraitPackets.PcTraitCurSpOffset..]);
        }

        using var pw = new PacketWriter();
        pw.WriteCompactLength(TraitPackets.PcTraitBlobLen);   
        pw.WriteBytes(blob);
        return pw.ToArray();
    }

 /// <param name="liveVitals">The live current HP and SP of the player. Null derives them from the stored character, which is correct only at world entry.</param>
    internal static byte[] BuildPcTrait2498FromDonor(ReadOnlySpan<byte> donorBody, Character character,
        EquipStats? equip = null, GameDataStore? store = null, (int Hp, int Sp)? liveVitals = null)
    {
        
        var template = donorBody[PcTraitPrefixLen..];

        
        
        
        
        if (store is not null)
            equip = KharaTitleTraits.Fold(equip, store, character.Id);

        
        
        
        
        
        
        ushort P(int baseStat, byte traitType)
            => (ushort)Math.Clamp(equip?.Apply(traitType, baseStat) ?? baseStat, 0, ushort.MaxValue);
        ushort[] primary = {
            P(character.Str, TraitStr), P(character.Agi, TraitAgi), P(character.Int, TraitInt),
            P(character.Wis, TraitWis), P(character.Vit, TraitVit), P(character.Luk, TraitLuk),
        };

        
        
        
        
        var baseStats = store is not null
            ? StatService.ComputeBaseStats(store, character.Job, character.Level,
                primary[0], primary[1], primary[2], primary[3], primary[4], primary[5])
            : default;
        bool haveBase = baseStats.MaxHp > 0;

        
        
        
        
        
        
        var maxHp = haveBase ? (uint)Math.Clamp((long)baseStats.MaxHp + (equip?.FlatOf(TraitMaxHp) ?? 0), 0, uint.MaxValue)
            : (uint)Math.Max(0, character.MaxHp > 0 ? character.MaxHp : FallbackMaxHpBase + character.Level * FallbackMaxHpPerLevel);
        
        var maxSp = haveBase ? (ushort)Math.Clamp(baseStats.MaxSp + (equip?.FlatOf(TraitMaxSp) ?? 0), 0, ushort.MaxValue)
            : (ushort)Math.Clamp(character.MaxMp > 0 ? character.MaxMp : FallbackMaxSpBase + character.Level * FallbackMaxSpPerLevel, 0, ushort.MaxValue);

        
        
        
        
        
        
        
        
        uint curHp;
        ushort curSp;
        if (liveVitals is { } live)
        {
            curHp = (uint)Math.Min((uint)Math.Max(0, live.Hp), maxHp);
            curSp = (ushort)Math.Min(Math.Max(0, live.Sp), maxSp);
        }
        else
        {
            bool fullHp = character.Hp <= 0 || character.Hp >= character.MaxHp;
            bool fullSp = character.Mp <= 0 || character.Mp >= character.MaxMp;
            curHp = fullHp ? maxHp : (uint)Math.Min((uint)character.Hp, maxHp);
            curSp = fullSp ? maxSp : (ushort)Math.Min(character.Mp, maxSp);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        var scaleAtBlob68 = equip is { } worn
            ? AutoAttackReachBase + worn.FlatOf(TraitAutoAttackRange)
            : System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(template[TraitPackets.PcTraitScale68Offset..]);

        
        
        
        
        
        
        
        
        
        
        
        IReadOnlyDictionary<byte, int>? derived = equip?.Flat;
        if (haveBase)
        {
            var merged = new Dictionary<byte, int>(baseStats.Derived);
            if (equip is { } e)
                foreach (var kv in e.Flat)
                    merged[kv.Key] = merged.GetValueOrDefault(kv.Key) + kv.Value;
            derived = merged;
        }

        
        
        
        
        
        
        
        var (bonus5, unspentStatPoints) = CharacterHeadRules.BonusStatBlock(character);

        var (hpRegen, spRegen) = RegenAmounts(store, character.Job, primary[4], primary[3], equip);

        var body = TraitPackets.BuildPcTrait2498(
            maxHp, maxSp, curHp, curSp, scaleAtBlob68, template,
            derivedByStatId: derived, primaryStats6: primary, bonusStatPoints: unspentStatPoints,
            bonusStats5: bonus5, hpRegen78: hpRegen, spRegen80: spRegen);

        
        
        
        
        if (store is not null)
            PcTraitRates.Stamp(body, character.Level, null);
        return body;
    }

 /// <summary>
 /// Regeneration amounts for HP and SP, from the class's base stat row and the effective VIT and WIS.
 /// </summary>
 /// <remarks>
 /// Returns null for both when there is no game data or no matching row, and the template values are kept.
 /// </remarks>
    internal static (ushort? Hp, ushort? Sp) RegenAmounts(GameDataStore? store, byte job, int vit, int wis,
        EquipStats? equip)
        => RegenAmounts(store is not null && store.PcBaseStatus.TryGetValue(job, out var row) ? row : null, vit, wis, equip);

 /// <summary>Regeneration amounts for the given base stat row. Null for both when the row is missing.</summary>
    internal static (ushort? Hp, ushort? Sp) RegenAmounts(RO2.GameData.Tables.PCBaseStatus? pc, int vit, int wis,
        EquipStats? equip)
    {
        if (pc is null) return (null, null);
        static decimal Coef(float c) => decimal.Round((decimal)(double)c, RegenCoefficientDecimals, MidpointRounding.ToEven);
        long hp = (long)decimal.Floor(Math.Max(0, vit) * Coef(pc.AutoHpRestorePerVit)) + pc.BaseHpRestoreAdd + (equip?.FlatOf(TraitHpRestore) ?? 0);
        long sp = (long)decimal.Floor(Math.Max(0, wis) * Coef(pc.AutoSpRestorePerWis)) + pc.BaseSpRestoreAdd + (equip?.FlatOf(TraitSpRestore) ?? 0);
        return ((ushort)Math.Clamp(hp, 0, ushort.MaxValue), (ushort)Math.Clamp(sp, 0, ushort.MaxValue));
    }
}
