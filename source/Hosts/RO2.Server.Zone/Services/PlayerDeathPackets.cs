using System.Buffers.Binary;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for a player's death: the victim's OnDeadMe (2391), the revival info (2836/2837), and the
/// state-6 OnChangeState (2398) sent to the map.
/// </summary>
/// <remarks>
/// The death sequence is: the state-6 change before the lethal damage, the armour wear, OnDeadMe (2391),
/// the revival info (2836/2837), removal of the after-death effects, a second state-6 change, and the
/// OnDeadNotify (2392) to the map.
/// </remarks>
public static class PlayerDeathPackets
{
 /// <summary>2398 state byte of a player character that has died.</summary>
    public const byte StateDead = 6;

 /// <summary>Blob byte 19 of a 2398 sent on a death in a field map.</summary>
 /// <remarks>
 /// Maps of the Map_PK 1 type use a different form of this value that is not reproduced; deaths there use the
 /// field-map form.
 /// </remarks>
    private const uint DeadStateTrailerFieldMap = 2;

 /// <summary>Zero bytes at blob+13 and blob+14 of a 2398 dead-state frame.</summary>
    private const byte DeadStateReserved = 0;

 /// <summary>Blob+15 (u32) of a 2398 dead-state frame: 0 on a field map. Maps of the Map_PK 1 type send 1.</summary>
    private const uint DeadStatePkMapFlagNone = 0;

 /// <summary>Blob+23 (u32) of a 2398 dead-state frame. Always 0.</summary>
    private const uint DeadStateTailNone = 0;

 /// <summary>InventoryItem.StorageType of worn equipment (0 is a bag cell).</summary>
    private const byte StorageTypeEquipped = 1;

 /// <summary>Size of the u32 inner-length prefix of a U32-family body such as 2391 or 2310.</summary>
    private const int InnerLengthPrefixSize = 4;

 /// <summary>Size of the u32 victim handle that opens the 2391 body after its length prefix.</summary>
    private const int VictimHandleSize = 4;

 /// <summary>Size of the 2391 tail header after the head: a u8 separator (always 0) and the u16 record count.</summary>
    private const int DeadMeRecordHeaderSize = 1 + 2;

 /// <summary>The u8 between the character head and the record count in 2391. Always 0.</summary>
    private const byte DeadMeHeadSeparator = 0;

 /// <summary>Length of the 2310 character head carried by 2391 (the same 205 bytes carried by 2336).</summary>
    private const int CharacterHeadLength = 205;

 /// <summary>Character uid (u64) at the start of the head, followed by the entity id (u32).</summary>
    private const int HeadUidOffset = 0;
    private const int HeadEntityIdOffset = 8;

 /// <summary>Character level within the head.</summary>
    private const int HeadLevelOffset = 69;

 /// <summary>
 /// Base EXP within the current level, written into the head. Zero at the level cap.
 /// </summary>
 /// <remarks>
 /// Below the cap this is the live value of <see cref="PlayerState.Exp"/>. The client copies the whole head over its
 /// character record, so a stale login value would reset the EXP bar.
 /// </remarks>
    private const int HeadBaseExpOffset = 70;

 /// <summary>
 /// The u64 written at <see cref="HeadBaseExpOffset"/>: the within-level EXP below
 /// <see cref="CompensationPackets.LevelCap"/>, and 0 at or above it.
 /// </summary>
    internal static ulong BaseExpForHead(PlayerState victim) =>
        victim.Level >= CompensationPackets.LevelCap ? 0UL : victim.Exp;

 /// <summary>
 /// Two u32 fields of the head (HP- and SP-shaped in the 2310 head). The victim's live values are written here;
 /// they are zero by the time the death message is built.
 /// </summary>
    private const int HeadCurHpOffset = 81;
    private const int HeadCurSpOffset = 85;

 /// <summary>Zeny wallet (u64) inside the head.</summary>
    private const int HeadWalletOffset = 112;

 /// <summary>Save-point bind index and facing direction inside the head.</summary>
    private const int HeadBindIndexOffset = 105;
    private const int HeadFacingOffset = 107;

 /// <summary>Remaining talent points inside the head.</summary>
    private const int HeadTalentPointsOffset = 142;

 /// <summary>f32 spinel revival cooldown of a 2836 body: 0.0 when none is running.</summary>
    private const float SpinelRevivalCooldownNone = 0f;

 /// <summary>Leading f32 of a 2837 body. 0.0.</summary>
    private const float PcCafeRevivalLeadNone = 0f;

 /// <summary>u8 of a 2837 body. 0.</summary>
    private const byte PcCafeRevivalFlagNone = 0;

 /// <summary>Trailing u16 of a 2837 body. 0.</summary>
    private const ushort PcCafeRevivalTailNone = 0;

 /// <summary>Builds the 2836 NfySpinelRevivalInfo body, <c>[f32 cooldown]</c> (4 bytes), with no cooldown running.</summary>
    internal static byte[] BuildSpinelRevivalInfo2836()
    {
        using var pw = new PacketWriter(4);
        pw.WriteSingle(SpinelRevivalCooldownNone); // f32 cooldown
        return pw.ToArray();
    }

 /// <summary>Builds the 2837 NfyPCCafeRevivalInfo body, <c>[f32][u8][u16]</c> (7 bytes), all zero, as sent at map load.</summary>
    internal static byte[] BuildPcCafeRevivalInfo2837()
    {
        using var pw = new PacketWriter(7);
        pw.WriteSingle(PcCafeRevivalLeadNone); // f32
        pw.WriteByte(PcCafeRevivalFlagNone);   // u8
        pw.WriteUInt16(PcCafeRevivalTailNone); // u16
        return pw.ToArray();
    }

 /// <summary>2836 NfySpinelRevivalInfo body: an f32 spinel revival cooldown, 0.0 when none is running.</summary>
    internal static readonly byte[] SpinelRevivalNone2836 = BuildSpinelRevivalInfo2836();

 /// <summary>2837 NfyPCCafeRevivalInfo body, <c>[f32][u8][u16]</c> (7 bytes), all zero.</summary>
    internal static readonly byte[] PcCafeRevivalNone2837 = BuildPcCafeRevivalInfo2837();

 /// <summary>
 /// 2398 OnChangeState for a dead player character, 29-byte body:
 /// <c>[01 1b][u16 27][u16 0xE2EA][u32 handle][u32 handle][u8 6][00][00][u32 0][u32 2][u32 0]</c>.
 /// </summary>
 /// <remarks>
 /// The subject handle is repeated. For a PC handle the body equals
 /// <c>CombatPackets.BuildChangeState2398(handle, 6, 0, 2)</c>. Broadcast to the map, including the dying character.
 /// </remarks>
    public static byte[] BuildOnChangeStateDead2398(uint pcHandle)
    {
        using var pw = new PacketWriter(32);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(CombatPackets.MsgTagChangeState); // blob+2  msgTag 0xE2EA
            p.WriteUInt32(pcHandle);                        // blob+4  PC subject, mirrors blob+8
            p.WriteUInt32(pcHandle);                        // blob+8  subject
            p.WriteByte(StateDead);                         // blob+12 state 6
            p.WriteByte(DeadStateReserved);                 // blob+13
            p.WriteByte(DeadStateReserved);                 // blob+14
            p.WriteUInt32(DeadStatePkMapFlagNone);          // blob+15 1 on Map_PK 1 maps, not modelled
            p.WriteUInt32(DeadStateTrailerFieldMap);        // blob+19
            p.WriteUInt32(DeadStateTailNone);               // blob+23
        });
        return pw.ToArray();
    }

 /// <summary>
 /// 2391 OnDeadMe, U32 family:
 /// <c>[u32 innerLen][u32 victimHandle][205-byte character head][u8 0][u16 n][n x ItemRecord126]</c>.
 /// </summary>
 /// <remarks>
 /// The head is the victim's 2310 head with the live state applied: level, wallet, the position of the death,
 /// and the talent point count, which the client copies over its character record. The records are the worn
 /// equipment only, in ascending slot order, with their current durability.
 /// </remarks>
    public static byte[] BuildOnDeadMe2391(PlayerState victim, IReadOnlyList<byte[]> wornRecords, byte talentPoints)
    {
        using var pw = new PacketWriter(InnerLengthPrefixSize + VictimHandleSize + CharacterHeadLength + DeadMeRecordHeaderSize
            + ItemPackets.ItemRecordLength * wornRecords.Count);
        var lenPos = pw.Reserve(InnerLengthPrefixSize);
        var start = pw.Length;
        pw.WriteUInt32(victim.EntityId);
        pw.WriteBytes(BuildDeathHead(victim, talentPoints));
        pw.WriteByte(DeadMeHeadSeparator);
        pw.WriteUInt16((ushort)wornRecords.Count);
        foreach (var record in wornRecords)
        {
            if (record.Length != ItemPackets.ItemRecordLength)
                throw new InvalidOperationException(
                    $"2391 carries {ItemPackets.ItemRecordLength}-byte item records; got {record.Length}.");
            pw.WriteBytes(record);
        }
        pw.WriteAt(lenPos, (uint)(pw.Length - start));
        return pw.ToArray();
    }

 /// <summary>
 /// The worn-gear records for 2391, in ascending slot order. Only equipment storage items are included, so bag
 /// items are never placed in the message.
 /// </summary>
    public static List<byte[]> BuildWornRecords(IEnumerable<InventoryItem> equipment)
        => equipment
            .Where(i => i.StorageType == StorageTypeEquipped)
            .OrderBy(i => i.Slot)
            .Select(WorldEntryService.BuildItemRecord126)
            .ToList();

 /// <summary>
 /// Sends the victim's part of a death: OnDeadMe (2391), then the revival info (2836 and 2837), then the failure
 /// notices of the held quests that the death fails. The worn gear is read after the lethal hit's armour wear
 /// has been persisted.
 /// </summary>
 /// <remarks>
 /// If the equipment read fails, the 2391 is still sent with an empty record vector, so the client can open its
 /// death and revival UI. The caller logs this as a degraded death.
 /// </remarks>
 /// <returns><c>false</c> when the equipment read failed and the record vector was sent empty.</returns>
    public static async Task<bool> SendDeadMeAsync(ProudSession session, PlayerState victim,
        IInventoryRepository inventory, GameDataStore gameData, CancellationToken ct = default)
    {
        List<byte[]> records;
        bool complete = true;
        try
        {
            records = BuildWornRecords(await inventory.GetEquipmentAsync(victim.CharacterId));
        }
        catch (Exception)
        {
            records = new List<byte[]>();
            complete = false;
        }

        // head+142 carries the live remaining talent points, computed as in the 2487 heads.
        var talentPoints = StatService.TalentPointsRemaining(gameData, victim.Job, victim.Level,
            victim.LearnedSkills.Select(kv => (kv.Key, kv.Value)));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnDeadMe, BuildOnDeadMe2391(victim, records, talentPoints), ct);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfySpinelRevivalInfo, SpinelRevivalNone2836, ct);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCCafeRevivalInfo, PcCafeRevivalNone2837, ct);

        // A held quest whose Quest_Info has a Fail_Type 26 row ("Player is dead.") fails with this death: a 2434
        // [quest][21714][26][0] goes to the victim.
        await QuestFailTriggers.OnPlayerDiedAsync(session, victim, null, ct);
        return complete;
    }

 /// <summary>
 /// Builds the 205-byte character head: the 2310 writer run over the character snapshot, with the live map,
 /// position, level, base EXP, HP, SP and wallet applied. Without a snapshot only the uid, entity id and the
 /// stamped fields are written. <paramref name="talentPoints"/> is written as the remaining talent points.
 /// </summary>
    private static byte[] BuildDeathHead(PlayerState victim, byte talentPoints)
    {
        var head = new byte[CharacterHeadLength];
        if (victim.Character is { } ch)
        {
            using var scratch = new PacketWriter(2048);
            WorldEntryService.WriteCharacterDetail(scratch, ch, victim.CharacterUid, victim.EntityId,
                items: null, victim.MapId, victim.X, victim.Y, victim.Z, talentPointsRemaining: talentPoints);
            scratch.GetWrittenSpan().Slice(InnerLengthPrefixSize, CharacterHeadLength).CopyTo(head);   // skip the 2310's u32 innerLen
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(HeadUidOffset), victim.CharacterUid);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(HeadEntityIdOffset), victim.EntityId);
            head[HeadTalentPointsOffset] = talentPoints;
        }
        head[HeadLevelOffset] = victim.Level;
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(HeadBaseExpOffset), BaseExpForHead(victim));   // live; 0 at the cap
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(HeadCurHpOffset), (uint)Math.Max(0, victim.Hp));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(HeadCurSpOffset), (uint)Math.Max(0, victim.Mp));
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(HeadWalletOffset), (ulong)Math.Max(0, victim.Gold));
        // The live save-point bind and facing direction.
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(HeadBindIndexOffset), victim.BindIndex);
        head[HeadFacingOffset] = WorldEntryService.FacingByte(victim.FacingYaw);
        return head;
    }
}
