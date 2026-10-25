using RO2.Network.Packets;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for single player-state notifications that the client dispatches through the compact-length
/// byte-array stubs.
/// </summary>
/// <remarks>
/// Every builder here writes the compact byte-array body:
/// <code>
/// body = [compactLen(selfLen)][u16 selfLen][u16 msgTag][fields...]
/// selfLen = 2 + payload.Length, payload = [u16 msgTag][fields]
/// </code>
/// <see cref="PacketWriter.WriteCompactBABody"/> writes the length marker, which the client's reader requires.
/// </remarks>
public static class ActorStatePackets
{
    // Message tags: the u16 at body+4 of each opcode, constant for every frame of that opcode.
    private const ushort TagOnSitDown = 58112;                      // 0xE300, RMI 2322
    private const ushort TagOnEmoticonAni = 58328;                  // 0xE3D8, RMI 2397
    private const ushort TagOnRevival = 58089;                      // 0xE2E9, RMI 2399
    private const ushort TagOnAckTransModeStart = 58405;            // 0xE425, RMI 2530
    private const ushort TagOnSkillChannelingCompleteNotify = 58289; // 0xE3B1, RMI 2586
    private const ushort TagNfyRideRidingPet = 58444;               // 0xE44C, RMI 2720
    private const ushort TagOnMountFarGlider = 58447;               // 0xE44F, RMI 2731
    private const ushort TagNfySkillMoveCancel = 58464;             // 0xE460, RMI 2747
    private const ushort TagNfySkillCastingCancel = 58441;          // 0xE449, RMI 2711
    private const ushort TagNfySkillMoveEnd = 58465;                // 0xE461, RMI 2749
    private const ushort TagOnChangeState = 58090;                  // 0xE2EA, RMI 2398
    private const ushort TagOnFuncObjectIn = 58350;                 // 0xE3EE, RMI 2512
    private const ushort TagOnUnmountFarGlider = 58448;             // 0xE450, RMI 2732

    // OnPCViewData record layout limits and field values.

    /// <summary>Longest character name, in UTF-16 characters, that the PC view record carries.</summary>
    private const int MaxNameChars = 21;

    /// <summary>Gender and race value written when the caller passes 0 (unset): the first gender and race.</summary>
    private const byte DefaultGenderOrRace = 1;

    /// <summary>
    /// Offset into the stored appearance bytes of the 13 bytes that fill the record's appearance fields. The
    /// first two stored bytes are not part of those fields.
    /// </summary>
    private const int AppearanceBytesOffset = 2;

    /// <summary>Count of appearance bytes copied into the record (appearance bytes 2 to 14).</summary>
    private const int AppearanceBytesLength = 13;

    /// <summary>Stored appearance length (15 bytes) a caller must pass for the appearance bytes to be copied.</summary>
    private const int AppearanceRawLength = 15;

    /// <summary>Move speed written when the caller names none.</summary>
    private const float DefaultMoveSpeed = 6.0f;

    /// <summary>Inventory storage type of worn gear; only worn items go into the record's equipment list.</summary>
    private const byte StorageTypeEquipped = 1;

    // Packed appearance u32: [level:6 @21][class:5 @16][job id:4 @12][job level:6 @6].
    private const int PackedLevelShift = 21;
    private const uint PackedLevelMask = 0x3F;
    private const int PackedClassShift = 16;
    private const uint PackedClassMask = 0x1F;
    private const int PackedJobIdShift = 12;
    private const uint PackedJobIdMask = 0x0F;
    private const int PackedJobLevelShift = 6;
    private const uint PackedJobLevelMask = 0x3F;

    /// <summary>
    /// Presence-mask word of the minimal PC record: only bit 9 is set. That bit gates one 4-byte optional field,
    /// written right after the mask.
    /// </summary>
    private const ushort PcRecordPresenceMask = 0x0200;

    /// <summary>Value of the 4-byte optional field gated by presence-mask bit 9.</summary>
    private const uint PcRecordPresenceField = 2;

    /// <summary>Constant dword of 2398 OnChangeState at blob+19.</summary>
    private const uint ChangeStateFixedDword = 2;

    /// <summary>Position-present flag of 2399 OnRevival: the trailing x, y, z floats hold the revive location.</summary>
    private const byte RevivalPositionPresent = 1;

 /// <summary>
 /// Model class byte sent in the character detail block for every character. It is a fixed donor value and must
 /// match WorldEntryService.Tail_Marker. Changing one without the other forces a model rebuild on the client.
 /// </summary>
    public const byte DetailTailModelClass = 0x10;

 /// <summary>
 /// Builds 2322 OnSitDown, the self acknowledgment of a sit or stand. <paramref name="state"/> is the sit flag
 /// from the C2S request (1 for sit, 0 for stand).
 /// </summary>
    public static byte[] BuildOnSitDown(uint actorHandle, byte state)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnSitDown);
            p.WriteUInt32(actorHandle);
            p.WriteUInt16(0);
            p.WriteByte(state);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2397 OnEmoticonAni, which plays an emote on an actor. Both handle slots are written with
 /// <paramref name="actorHandle"/>.
 /// </summary>
    public static byte[] BuildOnEmoticonAni(uint actorHandle, uint emoticonId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnEmoticonAni);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32(emoticonId);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2399 OnRevival, which revives the player at the given position. The message carries no HP or SP, so
 /// the caller restores vitals separately.
 /// </summary>
    public static byte[] BuildOnRevival(uint actorHandle, float x, float y, float z)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnRevival);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32(actorHandle);
            p.WriteByte(0);
            p.WriteByte(0);
            p.WriteByte(RevivalPositionPresent);
            p.WriteSingle(x);
            p.WriteSingle(y);
            p.WriteSingle(z);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2530 OnAckTransModeStart acknowledgment, with a zero result and no mode ID. The mode ID is sent in
 /// the separate 2531 notify.
 /// </summary>
    public static byte[] BuildOnAckTransModeStart()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnAckTransModeStart);
            p.WriteUInt32(0);
            p.WriteUInt16(0);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2586 OnSkillChannelingCompleteNotify for a caster and skill. Same shape as
 /// <see cref="CombatSkillNotifyPackets.BuildSkillChannelingCancelNotify2585"/>.
 /// </summary>
    public static byte[] BuildOnSkillChannelingCompleteNotify(uint casterHandle, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnSkillChannelingCompleteNotify);
            p.WriteUInt32(0);
            p.WriteUInt32(casterHandle);
            p.WriteUInt32(skillId);
        });
        return pw.ToArray();
    }

 /// <summary>Builds 2720 NfyRideRidingPet, announcing that a rider has mounted a pet.</summary>
    public static byte[] BuildNfyRideRidingPet(uint petId, uint riderHandle, uint petActorHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfyRideRidingPet);
            p.WriteUInt32(0);
            p.WriteUInt32(petId);
            p.WriteUInt32(riderHandle);
            p.WriteUInt32(petActorHandle);
        });
        return pw.ToArray();
    }

 /// <summary>Builds 2731 OnMountFarGlider, which mounts a rider on a glider.</summary>
    public static byte[] BuildOnMountFarGlider(uint riderHandle, ushort gliderId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnMountFarGlider);
            p.WriteUInt32(riderHandle);
            p.WriteUInt16(0);
            p.WriteUInt16(gliderId);
        });
        return pw.ToArray();
    }

 /// <summary>Message tag of 2315 OnPCViewData.</summary>
    private const ushort TagOnPCViewData = 0xE297;

 /// <summary>
 /// Builds 2315 OnPCViewData for <paramref name="player"/>. The body is the same PC record that 2504 OnPC_In
 /// carries for that player, written by <see cref="PlayerSpawnService.WriteRecord"/>. All live senders use this overload.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The client reads this body with the same record reader as 2504. The record carries the active effect list,
 /// the guild block, the refuse-review flag, the combat and creation class bits, and the move speed. Fields this
 /// server does not track are written as zero.
 /// </para>
 /// <para>
 /// Position, yaw, vitals, level, and name come from the live player and character, so the record is built at
 /// the moment it is sent.
 /// </para>
 /// </remarks>
 /// <param name="equipped">The PC's worn gear. It dresses the PC on other screens; the local client does not apply it.</param>
 /// <param name="emblems">The guild emblem registry used by the guild block.</param>
    public static byte[] BuildOnPCViewData2315(RO2.Server.Zone.Models.PlayerState player,
        RO2.Characters.Domain.Character character, IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? equipped,
        RO2.Application.Interfaces.IGuildEmblemRepository emblems)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnPCViewData);  // blob+2  msgTag 58007
            p.WriteUInt32(0);                // blob+4  header dword, always 0
            p.WriteUInt32(player.EntityId);  // blob+8  pcHandle; equals rec+0
            PlayerSpawnService.WriteRecord(p, player, character, emblems, equipped);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2315 OnPCViewData from loose values. This is the minimal record used on the glider re-dress path. It
 /// carries no effect list, guild block, or review flag, and it places <paramref name="job"/> in both the class
 /// and the profession fields.
 /// </summary>
 /// <remarks>
 /// The record holds the required identity fields, the live vitals, position, and speed, and empty optional
 /// sections. Equipment is not shown.
 /// </remarks>
 /// <param name="appearanceRaw">
 /// The character's stored appearance bytes. Bytes 2 to 14 fill the appearance fields. A null or short value
 /// leaves them zero.
 /// </param>
 /// <param name="modelClass">
 /// Model class byte. For the local player it must equal the value sent in the character detail block
 /// (<see cref="DetailTailModelClass"/>). A record about a remote player needs that player's own value.
 /// </param>
 /// <param name="masterLevel">Master level, written as a u16.</param>
    public static byte[] BuildOnPCViewData2315(uint actorHandle, ulong characterGid, string name, byte race, byte gender,
        float x, float y, float z, float yaw, uint curHp, uint maxHp, ushort curSp, ushort maxSp, byte level,
        byte job, byte jobLevel, IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? equipped,
        float moveSpeed = DefaultMoveSpeed, byte[]? appearanceRaw = null, byte modelClass = 0, ushort masterLevel = 0)
    {
        int nameLen = System.Math.Min(name?.Length ?? 0, MaxNameChars);
        var nameW = System.Text.Encoding.Unicode.GetBytes((name ?? string.Empty).Substring(0, nameLen));

        // Packed appearance u32: character level, combat class, job id and job level in fixed bit slots. The
        // combat class takes the low 5 bits of job and the job id its low 4 bits.
        uint packed =
              ((uint)(level    & PackedLevelMask)    << PackedLevelShift)
            | ((uint)(job      & PackedClassMask)    << PackedClassShift)
            | ((uint)(job      & PackedJobIdMask)    << PackedJobIdShift)
            | ((uint)(jobLevel & PackedJobLevelMask) << PackedJobLevelShift);

        // Worn gear fills the record's equipment list, which dresses this PC on other clients' screens. The
        // active-effect list is separate and is written empty below.
        var gear = equipped is null
            ? new System.Collections.Generic.List<RO2.Inventory.Domain.InventoryItem>()
            : System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(equipped, i => i.StorageType == StorageTypeEquipped));

        // Minimal PC record: identity, vitals, position and speed are the live values; every optional section
        // is empty (presence mask with bit 9 only, no status record, no guild, empty effect list and u32 list).
        // Every count word is a mandatory read and must be present even when zero.
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnPCViewData);     // blob+2  msgTag 58007
            p.WriteUInt32(0);                   // blob+4  header dword
            p.WriteUInt32(actorHandle);         // blob+8  pcHandle (low byte 0x11)
            // --- PC record head (70 bytes, record+0..69) ---
            p.WriteUInt32(actorHandle);         // rec+0  actor handle; equals pcHandle for the local player
            p.WriteUInt64(characterGid);        // rec+4  8-byte character GID
            p.WriteByte(0);                     // rec+12
            p.WriteByte(0);                     // rec+13
            p.WriteSingle(x);                   // rec+14 posX
            p.WriteSingle(y);                   // rec+18 posY
            p.WriteSingle(z);                   // rec+22 posZ
            // The body model is selected from (gender, race) in that order, so rec+26 is gender and rec+27 race.
            p.WriteByte(gender == 0 ? DefaultGenderOrRace : gender); // rec+26 gender
            p.WriteByte(race == 0 ? DefaultGenderOrRace : race);     // rec+27 race
            // rec+28..40 = stored appearance bytes 2..14 (see appearanceRaw); zero when none is passed.
            if (appearanceRaw is { Length: >= AppearanceRawLength })
                p.WriteBytes(appearanceRaw.AsSpan(AppearanceBytesOffset, AppearanceBytesLength));
            else
                p.WriteZeros(AppearanceBytesLength);
            p.WriteUInt32(curHp);               // rec+41 curHp
            p.WriteUInt32(maxHp);               // rec+45 maxHp
            p.WriteUInt16(curSp);               // rec+49 curSp
            p.WriteUInt16(0);                   // rec+51 pad
            p.WriteUInt16(maxSp);               // rec+53 maxSp
            p.WriteUInt16(0);                   // rec+55 pad
            p.WriteSingle(moveSpeed);           // rec+57 move speed
            p.WriteSingle(yaw);                 // rec+61 yaw
            // rec+65 is compared with the value the client already holds for this PC; a mismatch re-runs the
            // model rebuild, so it has to agree with it (see modelClass).
            p.WriteByte(modelClass);            // rec+65 model class
            p.WriteByte(0);                     // rec+66
            p.WriteUInt16(masterLevel);         // rec+67 master level
            p.WriteByte(0);                     // rec+69
            // --- name ---
            p.WriteByte((byte)nameLen);         // rec+70 name length in UTF-16 characters (at most MaxNameChars)
            p.WriteBytes(nameW);                // name as UTF-16, no terminator
            // --- packed appearance u32 (level, class, job id, job level) ---
            p.WriteUInt32(packed);
            // --- presence mask (bit 9 only) followed by the 4-byte field that bit gates ---
            p.WriteUInt16(PcRecordPresenceMask);
            p.WriteUInt32(PcRecordPresenceField);
            // --- status sub-record: flag 0 = empty ---
            p.WriteByte(0);
            // --- guild sub-record: name length 0 = no guild ---
            p.WriteByte(0);
            // --- equipment list: [u16 count][count x 28-byte element] ---
            p.WriteUInt16((ushort)gear.Count);
            foreach (var it in gear)
                EquipViewNotifyPackets.WriteEquipRecord28(p, it);
            // --- active-effect list: [u16 count][count x 23-byte element]; empty in this overload ---
            p.WriteUInt16(0);
            // --- tail dword ---
            p.WriteUInt32(0);
            // --- trailing u32 list: count 0 ---
            p.WriteUInt16(0);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2398 OnChangeState for an actor, sent to an arriving client before the re-dressing 2315. Only the actor
 /// handle varies; the rest of the body is fixed.
 /// </summary>
    public static byte[] BuildOnChangeState2398(uint actorHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnChangeState);  // blob+2  msgTag 58090
            p.WriteUInt32(actorHandle);       // blob+4  envelope actor handle
            p.WriteUInt32(actorHandle);       // blob+8  subject actor handle
            p.WriteByte(0);                   // blob+12 state enum (0)
            p.WriteByte(0);                   // blob+13
            p.WriteByte(0);                   // blob+14 flag
            p.WriteUInt32(0);                 // blob+15
            p.WriteUInt32(ChangeStateFixedDword); // blob+19 fixed value
            p.WriteUInt32(0);                 // blob+23
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds an empty 2512 OnFuncObject_In list (count 0), which keeps the glider arrival sequence in order.
 /// </summary>
    public static byte[] BuildFuncObjectInEmpty2512()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnFuncObjectIn);  // blob+2 msgTag 58350
            p.WriteUInt32(0);                  // blob+4 header dword
            p.WriteUInt16(0);                  // blob+8 count = 0
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2732 OnUnmountFarGlider, which dismounts the rider on arrival and closes the ride started by 2731.
 /// </summary>
 /// <remarks>
 /// The zeny value is the rider's balance after the flight fare. Pass the live <c>player.Gold</c>; the glider
 /// handler has already taken the fare.
 /// </remarks>
    public static byte[] BuildUnmountFarGlider2732(uint riderHandle, long zeny)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnUnmountFarGlider);    // blob+2  msgTag 58448
            p.WriteUInt32(riderHandle);              // blob+4  rider PC handle
            p.WriteUInt64((ulong)Math.Max(0, zeny)); // blob+8  wallet balance after the fare
            p.WriteUInt16(0);                        // blob+16 always 0
        });
        return pw.ToArray();
    }

 /// <summary>Builds 2747 NfySkillMoveCancel, which cancels a skill movement for a caster.</summary>
    public static byte[] BuildNfySkillMoveCancel(uint casterHandle, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfySkillMoveCancel);
            p.WriteUInt32(0);
            p.WriteUInt32(casterHandle);
            p.WriteUInt32(skillId);
        });
        return pw.ToArray();
    }

 /// <summary>Builds 2711 NfySkillCastingCancel, which cancels a skill cast for a caster.</summary>
    public static byte[] BuildNfySkillCastingCancel(uint casterHandle, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfySkillCastingCancel);
            p.WriteUInt32(0);
            p.WriteUInt32(casterHandle);
            p.WriteUInt32(skillId);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2749 NfySkillMoveEnd for an NPC caster, with skill 0 and the NPC move kind. The skill is ignored for
 /// NPC casters.
 /// </summary>
 /// <remarks>
 /// Delegates to <see cref="BuildNfySkillMoveEnd(uint, uint, byte, float, float, float, float)"/>.
 /// </remarks>
    public static byte[] BuildNfySkillMoveEnd(uint casterHandle, float x, float y, float z, float dir)
        => BuildNfySkillMoveEnd(casterHandle, 0, RetailNpcMoveKind, x, y, z, dir);

 /// <summary>Move kind used for NPC casters in 2749.</summary>
    internal const byte RetailNpcMoveKind = 0x0E;

 /// <summary>
 /// Builds 2749 NfySkillMoveEnd, which ends a skill movement and places the caster at the given position and yaw.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The client looks up the actor, and for a player caster it applies the position only when it finds the
 /// completed skill with this ID. The skill ID and move kind are the client's own values for the ended skill.
 /// </para>
 /// </remarks>
    public static byte[] BuildNfySkillMoveEnd(uint casterHandle, uint skillId, byte moveKind, float x, float y, float z, float dir)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfySkillMoveEnd);
            p.WriteUInt32(0);
            p.WriteUInt32(casterHandle);
            p.WriteUInt32(skillId);
            p.WriteByte(moveKind);
            p.WriteSingle(x);
            p.WriteSingle(y);
            p.WriteSingle(z);
            p.WriteSingle(dir);
        });
        return pw.ToArray();
    }
}
