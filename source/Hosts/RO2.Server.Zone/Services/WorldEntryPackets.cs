using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Skills.Domain;
using Microsoft.Extensions.Logging;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using System.Text;

namespace RO2.Server.Zone.Services;

/// <summary>
/// World-entry wire builders split out of <see cref="WorldEntryService"/> (B5 Phase 0, S1a): byte-for-byte frame
/// construction with no state. Part of the World context's host wire layer — stays host behind the wire boundary.
/// </summary>
internal static class WorldEntryPackets
{
 /// <summary>Fixed first byte of the map-load body.</summary>
    private const byte MapLoadLeadByte = 0x01;
 /// <summary>Fixed u16 at offset 1 of the map-load body.</summary>
    private const ushort MapLoadMarkerA = 0x2B2B;
 /// <summary>Fixed u16 at offset 4 of the map-load body.</summary>
    private const ushort MapLoadMarkerB = 0xE294;
 /// <summary>Last byte of the map-load body (offset 44). This value is known to spawn the client; the byte is not otherwise identified.</summary>
    private const byte MapLoadTrailerByte = 0xAB;

 /// <summary>Length of the 2311 map-load body (45 bytes).</summary>
    internal const int MapLoad2311Length = 45;

 /// <summary>
 /// Builds the 45-byte map-load body that puts the client on a map at a position.
 /// </summary>
 /// <remarks>
 /// Layout: a fixed header (offsets 0 to 5), the actor handle at offset 6, the map id as a u32 at offset 10, 12 zero bytes, the X, Y and Z position as three floats at offset 26, two zero bytes, the actor handle again at offset 40, and a trailer byte at offset 44.
 /// </remarks>
    internal static byte[] BuildMapLoad2311(uint actorHandle, uint mapId, float x, float y, float z)
    {
        using var tw = new PacketWriter();
        tw.WriteByte(MapLoadLeadByte);  // [0] fixed
        tw.WriteUInt16(MapLoadMarkerA);  // [1] fixed
        tw.WriteByte(0x00);  // [3] fixed zero
        tw.WriteUInt16(MapLoadMarkerB);  // [4] fixed
        tw.WriteUInt32(actorHandle);  // [6] actor handle
        tw.WriteUInt32(mapId);  // [10] map id

        tw.WriteZeros(12);  // [14] zeros
        tw.WriteSingle(x);  // [26] position X
        tw.WriteSingle(y);  // [30] position Y
        tw.WriteSingle(z);  // [34] position Z
        tw.WriteUInt16(0);  // [38] zeros
        tw.WriteUInt32(actorHandle);  // [40] actor handle again
        tw.WriteByte(MapLoadTrailerByte);  // [44] trailer byte
        return tw.ToArray();
    }

 /// <summary>
 /// A length-prefixed counted list with no entries: the u32 length word (2) followed by a u16 count of 0.
 /// </summary>
    internal static byte[] EmptyCountedList()
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(sizeof(ushort));   // u32 length: the bytes after it, only the count
        pw.WriteUInt16(0);                // u16 entry count
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the server time message: the local wall-clock time as microseconds from the client's epoch, eight bytes.
 /// </summary>
 /// <remarks>
 /// The client's time reader rejects an invalid instant, so the value must be a real clock reading. The time is the server's local time, not UTC.
 /// </remarks>
    internal static byte[] BuildServerTimeToLoginPC2669(DateTimeOffset localNow)
    {
        var body = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            body, ServerTimeCodec.ToClientMicroseconds(localNow));
        return body;
    }

 /// <summary>Default HP written to the 2310 head and stat block when no vitals are supplied: 224, a level-1 character's maximum.</summary>
    private const uint DefaultHp = 0x000000E0;
 /// <summary>Default SP written to the 2310 head and stat block when no vitals are supplied: 15, a level-1 character's maximum.</summary>
    private const uint DefaultSp = 0x0000000F;


 /// <summary>Fixed byte at <see cref="HeadOffset.Constant120"/>, sent unchanged to every client. Its meaning is not identified.</summary>
    private const byte HeadConstByte120 = 0x49;
 /// <summary>Fixed u32 at <see cref="HeadOffset.Constant122"/> (bytes 00 00 00 01), sent unchanged to every client. Its meaning is not identified.</summary>
    private const uint HeadConstField122 = 0x01000000;
    private const uint Tentative_Const_Field139 = 0x00000600;

 /// <summary>Length of the zero-padded name field of the 2310 head: 21 UTF-16 characters.</summary>
    private const int HeadNameBytes = 42;
 /// <summary>Most name bytes copied into the name field, so that the last character stays a terminator.</summary>
    private const int HeadNameCopyLimit = 40;
 /// <summary>Length of the look block of the 2310 head: the first 15 bytes of the stored creation block.</summary>
    private const int HeadAppearanceBytes = 15;
 /// <summary>Number of zero bytes that end the 2310 head (offsets 199 to 204).</summary>
    private const int HeadReservedBytes = 6;

 /// <summary>Value of the size word that opens the stat block of the 2310 message. The block is 28 bytes long, counting this word.</summary>
    private const uint StatBlock_Size  = 0x1C;
 /// <summary>Marker byte written twice, after a zero u32, in the fixed tail that ends the 2310 message.</summary>
    private const byte Tail_Marker     = 0x10;
 /// <summary>Number of zero bytes that close the fixed tail of the 2310 message.</summary>
    private const int TailPaddingBytes = 24;

 /// <summary>
 /// Fill byte of an unset visual slot in the 2310 head (offsets 165 to 172). Every byte of the region is this value
 /// when the character has no visual slot set.
 /// </summary>
    private const byte NoVisualSlot = 0xFF;

 /// <summary>
 /// The eight visual-slot bytes of the 2310 head (offsets 165 to 172), each <see cref="NoVisualSlot"/>. The region
 /// is eight bytes wide, and whether it is eight single bytes, four u16 or two u32 is not determined, so it is
 /// written as one run of eight fill bytes.
 /// </summary>
    private static ReadOnlySpan<byte> EmptyVisualSlots =>
    [
        NoVisualSlot, NoVisualSlot, NoVisualSlot, NoVisualSlot,
        NoVisualSlot, NoVisualSlot, NoVisualSlot, NoVisualSlot,
    ];

 /// <summary>
 /// Default skill ids of the 2310 skill list, a level-1 Swordsman's three class skills. Written only when no skill list is supplied.
 /// </summary>
    private const uint DonorVec1Skill0 = 110201;
    private const uint DonorVec1Skill1 = 111001;
    private const uint DonorVec1Skill2 = 114401;

 /// <summary>
 /// Byte offsets of the fields in the 205-byte head of the 2310 message, counted from the start of the message body
 /// (after its length word). <see cref="WriteCharacterHead205"/> writes the fields in this order.
 /// </summary>
    internal static class HeadOffset
    {
 /// <summary>u64 character uid.</summary>
        public const int CharUid = 0;
 /// <summary>u32 entity id.</summary>
        public const int EntityId = 8;
 /// <summary>Character name, 42 bytes of UTF-16.</summary>
        public const int Name = 12;
 /// <summary>Character look block, 15 bytes.</summary>
        public const int Appearance = 54;
 /// <summary>u8 character level.</summary>
        public const int Level = 69;
 /// <summary>u64 base experience.</summary>
        public const int BaseExp = 70;
 /// <summary>u8 combat class (job id).</summary>
        public const int CombatClass = 78;
 /// <summary>u32 current HP.</summary>
        public const int CurHp = 81;
 /// <summary>u32 current SP (the value is 16 bits wide).</summary>
        public const int CurSp = 85;
 /// <summary>f32 position X; Y follows at +4 and Z at +8.</summary>
        public const int PosX = 89;
 /// <summary>u32 map id.</summary>
        public const int MapId = 101;
 /// <summary>u16 save-point bind index (0 for none).</summary>
        public const int BindIndex = 105;
 /// <summary>u8 facing, in 2-degree steps.</summary>
        public const int Facing = 107;
 /// <summary>u64 zeny wallet.</summary>
        public const int Wallet = 112;
 /// <summary>Fixed byte, see <see cref="HeadConstByte120"/>.</summary>
        public const int Constant120 = 120;
 /// <summary>u8 bank slot count.</summary>
        public const int BankSlots = 121;
 /// <summary>Fixed u32, see <see cref="HeadConstField122"/>.</summary>
        public const int Constant122 = 122;
 /// <summary>u32 Khara point balance.</summary>
        public const int KharaPoints = 126;
 /// <summary>u8 profession picked at creation.</summary>
        public const int CreationProfession = 130;
 /// <summary>u8 job level.</summary>
        public const int JobLevel = 131;
 /// <summary>u64 job experience.</summary>
        public const int JobExp = 132;
 /// <summary>u16 equipment-hide flags.</summary>
        public const int EquipHideFlag = 140;
 /// <summary>u8 remaining talent (skill) points.</summary>
        public const int TalentPoints = 142;
 /// <summary>u32 time left on the title A timer.</summary>
        public const int TitleATimer = 143;
 /// <summary>u32 worn title A.</summary>
        public const int TitleA = 147;
 /// <summary>Five u16 allocated bonus stats: STR, AGI, INT, WIS, VIT.</summary>
        public const int BonusStats = 151;
 /// <summary>u16 unspent bonus stat points.</summary>
        public const int UnspentBonusPoints = 163;
 /// <summary>Eight visual-slot bytes.</summary>
        public const int VisualSlots = 165;
 /// <summary>u32 union id, then a u64 join time.</summary>
        public const int Union = 173;
 /// <summary>u32 worn title B.</summary>
        public const int TitleB = 185;
 /// <summary>u8 profession level.</summary>
        public const int ProJobLevel = 189;
 /// <summary>u8 profession id.</summary>
        public const int ProJobId = 190;
 /// <summary>u64 job-mastery point total.</summary>
        public const int JobMasteryPoints = 191;
 /// <summary>Six reserved zero bytes.</summary>
        public const int Reserved = 199;
 /// <summary>Total length of the head.</summary>
        public const int Length = 205;
    }

 /// <summary>
 /// Writes the 2310 character detail message for a character.
 /// </summary>
 /// <param name="vitals">Current HP and SP and their maximums. Null uses the default values.</param>
 /// <param name="skillList">Usable skills as (group, level) pairs. Null writes the default three ids.</param>
    internal static void WriteCharacterDetail(PacketWriter writer, Character character, ulong charUid, uint entityId,
        IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? items, uint mapId, float posX, float posY, float posZ,
        IReadOnlyList<RO2.Domain.Entities.CharacterQuest>? activeQuests = null,
        byte talentPointsRemaining = 0,
        GameDataStore? gameData = null,
        (uint CurHp, ushort CurSp, uint MaxHp, ushort MaxSp)? vitals = null,
        IReadOnlyList<(uint Group, byte Level)>? skillList = null)
    {
        var lengthPos = writer.Reserve(4);  // message length word, filled in at the end
        var dataStart = writer.Length;

        
        WriteCharacterHead205(writer, character, charUid, entityId, mapId, posX, posY, posZ, talentPointsRemaining,
            vitals is { } v ? (v.CurHp, v.CurSp) : null);

        
        
        
        
        
        
        
        if (skillList is not null)
        {
            var ids = skillList.Select(s => s.Group * SkillPackets.SkillIdGroupFactor + s.Level).Distinct().Order().ToArray();
            writer.WriteUInt16((ushort)ids.Length);
            foreach (var id in ids)
                writer.WriteUInt32(id);
        }
        else
        {
            writer.WriteUInt16(3);
            writer.WriteUInt32(DonorVec1Skill0);
            writer.WriteUInt32(DonorVec1Skill1);
            writer.WriteUInt32(DonorVec1Skill2);
        }

        
        var allItems = items ?? [];
        writer.WriteUInt16((ushort)allItems.Count);
        foreach (var item in allItems)
        {
            
            
            
            if (gameData is not null) writer.WriteBytes(ItemWorkbench.RecordWithRuneholes(item, gameData));
            else ItemPackets.WriteItemInstance(writer, item);
        }

        writer.WriteUInt16(0);  // empty list
        
        
        
        
        
        
        
        
        
        var qv = activeQuests ?? System.Array.Empty<RO2.Domain.Entities.CharacterQuest>();
        writer.WriteUInt16((ushort)qv.Count);
        foreach (var q in qv)
            QuestProgressStore.WriteLoginElement(writer, q, gameData, allItems);
        writer.WriteUInt16(0);  // empty list

        
        
        
        
        
        
        
        
        
        
        
        KharaService.WriteLoginVectors(writer, character.Id, gameData);

        
        
        
        
        writer.WriteUInt32(StatBlock_Size);  // stat block size word
        writer.WriteUInt32(entityId);  // entity id
        writer.WriteUInt32(entityId);  // entity id again
        writer.WriteUInt32(vitals?.CurHp ?? DefaultHp);  // current HP
        writer.WriteUInt32(vitals?.CurSp ?? DefaultSp);  // current SP
        writer.WriteUInt32(vitals?.MaxHp ?? DefaultHp);  // maximum HP
        writer.WriteUInt32(vitals?.MaxSp ?? DefaultSp);  // maximum SP

        writer.WriteUInt16(0);  // empty list

        
        writer.WriteUInt32(0);  // tail: zero word
        writer.WriteByte(Tail_Marker);  // tail marker
        writer.WriteByte(Tail_Marker);  // tail marker
        writer.WriteZeros(TailPaddingBytes);

        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));   
    }

 /// <summary>
 /// Writes the 205-byte base block of the 2310 character detail message, the part before the first list.
 /// </summary>
 /// <remarks>
 /// Messages that repeat the character head can write this block directly. Throws if the block does not come to exactly 205 bytes.
 /// </remarks>
 /// <param name="vitals">Current HP and SP. Null uses the default values.</param>
    internal static void WriteCharacterHead205(PacketWriter writer, Character character, ulong charUid, uint entityId,
        uint mapId, float posX, float posY, float posZ, byte talentPointsRemaining,
        (uint CurHp, ushort CurSp)? vitals = null)
    {
        var blobStart = writer.Length;

        
        writer.WriteUInt64(charUid);  // +0 character uid
        
        writer.WriteUInt32(entityId);  // +8 entity id

        
        var nameBuf = new byte[HeadNameBytes];
        var nameBytes = Encoding.Unicode.GetBytes(character.Name ?? "");
        Buffer.BlockCopy(nameBytes, 0, nameBuf, 0, Math.Min(nameBytes.Length, HeadNameCopyLimit));
        writer.WriteBytes(nameBuf);  // +12 name, UTF-16, zero padded

        
        
        var appearanceBuf = new byte[HeadAppearanceBytes];               
        var ap = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson);
        if (ap.RawBytes != null && ap.RawBytes.Length >= HeadAppearanceBytes)
            Buffer.BlockCopy(ap.RawBytes, 0, appearanceBuf, 0, HeadAppearanceBytes);
        writer.WriteBytes(appearanceBuf);  // +54 look bytes from the creation block

        
        writer.WriteByte(character.Level);  // +69 level

        
        WriteTentativeBaseExp(writer, character);  // +70 base experience (u64)
        writer.WriteByte(CharacterHeadRules.CombatJobByte(character));  // +78 combat class (job id)
        writer.WriteUInt16(0);  // +79 zeros

        
        
        
        
        
        
        
        
        writer.WriteUInt32(vitals?.CurHp ?? DefaultHp);  // +81 current HP
        writer.WriteUInt32(vitals is { } vs ? vs.CurSp : DefaultSp);  // +85 current SP (u32 holding the u16 value)

        
        writer.WriteSingle(posX);  // +89 position X
        writer.WriteSingle(posY);  // +93 position Y
        writer.WriteSingle(posZ);  // +97 position Z

        
        writer.WriteUInt32(mapId);  // +101 map id

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        writer.WriteUInt16(character.BindIndex);  // +105 save-point bind index (0 = none)
        
        writer.WriteByte(CharacterHeadRules.FacingByte(character.FacingYaw));  // +107 facing, 2-degree steps
        writer.WriteZeros(4);  // +108 zeros
        writer.WriteUInt64((ulong)Math.Max(0, character.Gold));  // +112 zeny wallet
        writer.WriteByte(HeadConstByte120);  // +120 fixed byte
        
        
        
        
        writer.WriteByte(BankExpansion.SlotsOf(character));  // +121 bank slot count
        writer.WriteUInt32(HeadConstField122);  // +122 fixed u32

        
        
        
        
        
        
        
        
        writer.WriteUInt32(KharaService.PublishedPoints(character.Id));  // +126 Khara point balance
        
        
        
        WriteTentativeJobBlock(writer, character);  // +130 profession, job level, job exp, equip-hide flags

        
        
        
        
        
        
        writer.WriteByte(talentPointsRemaining);  // +142 remaining talent points
        
        
        
        
        
        
        
        KharaTitleStore.WriteHeadTitleA(writer, character.Id, DateTime.UtcNow);  // +143 title A timer and title A, then 10 bytes filled below

        
        
        
        
        
        
        
        var (bonus5, unspentBonusPoints) = CharacterHeadRules.BonusStatBlock(character);
        for (int i = 0; i < bonus5.Length; i++)
            writer.WriteAt(blobStart + HeadOffset.BonusStats + 2 * i, bonus5[i]);  // +151 allocated bonus STR..VIT
        writer.WriteUInt16(0);  // +161 zeros
        writer.WriteUInt16(unspentBonusPoints);  // +163 unspent bonus points
        
        writer.WriteBytes(EmptyVisualSlots);  // +165 visual slots
        
        
        
        
        
        
        UnionMembershipStore.WriteHead(writer, character.Id);  // +173 union id and join time

        writer.WriteUInt32(KharaTitleStore.PublishedFor(character.Id)?.TitleB ?? 0u);  // +185 title B
        
        
        var proJob = CharacterSerializer.WireProJob(character);
        writer.WriteByte(proJob.Level);  // +189 profession level
        writer.WriteByte(proJob.Id);  // +190 profession id
        
        
        
        
        
        
        
        
        writer.WriteUInt64(CharacterHeadRules.JobMasteryPoints(character));  // +191 job-mastery point total
        
        
        
        
        
        writer.WriteZeros(HeadReservedBytes);  // +199 reserved

        var blobWritten = writer.Length - blobStart;
        if (blobWritten != HeadOffset.Length)
            throw new InvalidOperationException($"2310 base blob expected {HeadOffset.Length} bytes, wrote {blobWritten}");
    }

 /// <summary>
 /// Writes the base experience field of the character head. Stays zero until the character's experience is stored.
 /// </summary>
    private static void WriteTentativeBaseExp(PacketWriter writer, Character character)
    {
        writer.WriteUInt64(character.Exp);          
    }


 /// <summary>
 /// Writes the job block of the character head: profession, job level, job experience and the equipment-hide flags.
 /// </summary>
 /// <remarks>
 /// The caller writes the four-byte field that precedes this block.
 /// </remarks>
    private static void WriteTentativeJobBlock(PacketWriter writer, Character character)
    {
        writer.WriteByte(CharacterHeadRules.CreationProfessionByte(character));  // +130 profession picked at creation
        writer.WriteByte(character.JobLevel);  // +131 job level
        writer.WriteUInt64(character.JobExp);  // +132 job experience
        
        
        
        
        writer.WriteUInt16(character.EquipHideFlag);  // +140 equipment-hide flags
    }
}
