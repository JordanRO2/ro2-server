using System.Text;
using RO2.Skills.Domain;
using Microsoft.Extensions.Logging;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds and sends the server packet sequence for entering the world after login (RMI 1417).
/// </summary>
/// <remarks>
/// The sequence includes the character detail (2310), the map load (2311), and optional game time (2321) and login key (2733) packets.
/// </remarks>
public static class WorldEntryService
{
 /// <summary>Default starting map.</summary>
    public const uint DefaultMapId = 1101;
 /// <summary>Default spawn X position on the default starting map.</summary>
    public const float DefaultX = 209.34f;
    public const float DefaultY = 268.79f;
    public const float DefaultZ = 48.23f;
    private const string ChatHost = "127.0.0.1";
    private const ushort ChatPort = 7401;

    public static float GetDefaultX() => DefaultX;
    public static float GetDefaultY() => DefaultY;
    public static float GetDefaultZ() => DefaultZ;

 /// <summary>
 /// Resolves the map and position a character enters the world at.
 /// </summary>
 /// <remarks>
 /// Falls back to the default starting map when the character's map is 0 or is not in the map list.
 /// </remarks>
    public static (uint MapId, float X, float Y, float Z) ResolveSpawnPosition(
        Character character, GameDataStore? data)
    {
        var mapId = character.MapId;
        var x = character.X;
        var y = character.Y;
        var z = character.Z;

        
        var isValidMap = mapId != 0 && data?.GetMap(mapId) != null;

        if (!isValidMap)
        {
            mapId = DefaultMapId;
            x = DefaultX;
            y = DefaultY;
            z = DefaultZ;
        }
        else if (x == 0f && y == 0f && z == 0f)
        {
            
            var mapData = data?.GetMap(mapId);
            if (mapData != null && (mapData.x != 0 || mapData.y != 0 || mapData.z != 0))
            {
                x = mapData.x;
                y = mapData.y;
                z = mapData.z;
            }
            else
            {
                
                
                
                
                
            }
        }

        return (mapId, x, y, z);
    }

    public static async Task SendWorldEntryAsync(
        ProudSession session,
        Character character,
        ulong charUid,
        uint entityId,
        ILogger logger,
        IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? inventoryItems = null,
        IReadOnlyList<CharacterQuest>? activeQuests = null,
        int completedQuestCount = 0,
        IReadOnlyList<CharacterSkill>? learnedSkills = null,
        GameDataStore? gameData = null,
        IReadOnlyList<RO2.Characters.Domain.CharacterQuickSlot>? quickSlots = null,
        bool isWarp = false,
        (uint MapId, float X, float Y, float Z)? warpSpawn = null,
        CollectionDocument? collection = null,
        KeyConfigDocument? keyConfig = null,
        DateTime? cardBookChargedUntil = null,
        byte[]? warpPcTrait2498 = null,
        IReadOnlyCollection<ushort>? learnedCraftRecipes = null,
        IReadOnlyList<OwnedVehicle>? vehicles = null,
        IReadOnlyList<(uint Group, byte Level)>? useSkillList = null)
    {
        
        var (mapId, posX, posY, posZ) = ResolveSpawnPosition(character, gameData);

        
        
        
        
        
        
        
        if (isWarp && warpSpawn is { } w)
        {
            mapId = w.MapId; posX = w.X; posY = w.Y; posZ = w.Z;
        }

        
        
        
        
        
        
        
        
        
        EquipStats? equipDerived = gameData is not null && inventoryItems is not null
            ? StatService.ComputeEquipmentStats(gameData, inventoryItems)
            : null;

        
        
        
        byte[] pcTraitEntry;
        {
            
            
            
            
            
            
            
            
            pcTraitEntry = TraitPackets.BuildPcTrait2498FromDonor(TraitPackets.PcTrait2498Donor, character, equipDerived, gameData);
        }
        
        
        
        
        
        
        
        if (isWarp && warpPcTrait2498 is { Length: TraitPackets.PcTraitBodyLen })
            pcTraitEntry = warpPcTrait2498;

        
        await SendPreMapLoadBurstAsync(session, character, entityId, keyConfig, pcTraitEntry);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!isWarp && CharacterSerializer.WireProJob(character).Id == 0
            && gameData is not null
            && BuildNotifyCraftIndex2535(gameData.CraftItemList, CharacterHeadRules.CreationProfessionByte(character), learnedCraftRecipes) is { } craftIndex)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCraftIndex, craftIndex);

        
        
        
        
        
        
        
        var learnedPairs = (learnedSkills ?? (IReadOnlyList<CharacterSkill>)Array.Empty<CharacterSkill>())
            .Select(s => (s.SkillId, s.Level));
        byte talentRemaining = gameData is null
            ? (byte)Math.Clamp((character.Level - 1) - learnedPairs.Sum(p => (int)p.Level), 0, 255)
            : TalentRules.TalentPointsRemaining(gameData, character.Job, character.Level, learnedPairs);

        
        
        
        
        
        IReadOnlyList<(uint Group, byte Level)> loginSkills = useSkillList ?? learnedPairs.ToArray();

        if (!isWarp)
        {
            
            
            var vitals = TraitPackets.VitalsOf(pcTraitEntry);
            await session.SendRmiAsync(ZoneS2COpcodes.OnCharacterDetailACK, writer =>
            {
                WriteCharacterDetail(writer, character, charUid, entityId, inventoryItems, mapId, posX, posY, posZ, activeQuests, talentRemaining, gameData,
                    vitals: vitals, skillList: loginSkills);
            });
            logger.LogDebug("Session {Id}: Sent OnCharacterDetailACK (RMI 2310) ENCRYPTED", session.SessionId);
        }

        
        
        
        
        
        if (!isWarp)
        {
            
            
            
            
            
            await session.SendInternalRmiAsync(
                ZoneS2COpcodes.OnPcTrait, TraitPackets.BuildPcTrait2498FromDonor(TraitPackets.PcTrait2498Donor, character, equipDerived, gameData));
        }

        
        
        
        
        
        
        
        {
            
            
            
            
            
            
            
            
            
            if (!isWarp)
                await SendUseSkillList2488Async(session, loginSkills);
        }

        
        
        
        
        
        
        
        
        if (!isWarp && cardBookChargedUntil is { } chargedUntil)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyCardBookChargedTime,
                CardBookChargeStore.Build2793(chargedUntil, DateTime.Now));

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!isWarp)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyRankingTopList, EmptyCountedList());

        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveMapLoad, WorldEntryPackets.BuildMapLoad2311(entityId, mapId, posX, posY, posZ));
        logger.LogDebug("Session {Id}: Sent OnReceiveMapLoad (INT 2311) for map {MapId}", session.SessionId, mapId);

        
        
        
        
        
        
        
        
        
        
        
        
        
        {
            var slots = quickSlots ?? Array.Empty<RO2.Characters.Domain.CharacterQuickSlot>();
            using var tw = new PacketWriter();
            tw.WriteUInt32((uint)(QuickSlotListHeaderLength + QuickSlotRecordLength * slots.Count));
            tw.WriteUInt64(charUid);
            tw.WriteUInt16((ushort)slots.Count);
            foreach (var s in slots)
            {
                tw.WriteByte(s.Page);
                tw.WriteByte(s.Slot);
                tw.WriteByte(s.EntryType);
                tw.WriteByte(0);
                tw.WriteUInt32(s.EntryId);
                tw.WriteUInt32(s.Amount);
                tw.WriteUInt64(s.ItemUid);
            }
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnQuickSlotSaveData, tw.GetWrittenMemory());
            logger.LogDebug("Session {Id}: restored {Count} quick slot(s)",
                session.SessionId, slots.Count);
        }

        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnVehicleQuickSlot, VehiclePackets.BuildVehicleQuickSlot2523Empty());

        
        
        
        
        
        
        VehicleClientList.Record(session.SessionId, vehicles ?? Array.Empty<OwnedVehicle>());
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAllVehicleList,
            VehiclePackets.BuildAllVehicleList2522(vehicles ?? Array.Empty<OwnedVehicle>(), VehiclePackets.NowOle()));

        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyMCollection,
            BuildNfyMCollection2913(collection?.RegisteredMonsterIds.Count ?? 0, gameData));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCollection,
            CollectionPackets.BuildNfyPCollection(collection?.RegisteredPetIds.Count ?? 0));
        
        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPCollectionSetActivePet, collection is { ActivePetId: not 0 }
            ? CollectionPackets.BuildAnsPCollectionSetActivePet(collection.ActivePetId, collection.ActivePetFlag, 0)
            : CollectionPackets.BuildAnsPCollectionSetActivePet(0, 0, PCollectionNoActivePetResult));

        
        
        
        
        
        
        
        if (inventoryItems is not null && gameData is not null)
            foreach (var cardId in CardRegistrationNotifier.HeldMvpCards(inventoryItems, gameData))
                await session.SendRmiAsync(ZoneS2COpcodes.AnsRegistCardBook, CardPackets.BuildRegistCardBook2588(cardId));

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!isWarp)
        {
            await session.SendInternalRmiAsync(
                ZoneS2COpcodes.OnChangeActorMoveSpeed, TraitPackets.BuildActorMoveSpeed2497(entityId, TraitPackets.PcModalWorldEntrySpeed));
            logger.LogDebug("Session {Id}: Sent OnChangeActorMoveSpeed (INT 2497) speed={Speed}",
                session.SessionId, TraitPackets.PcModalWorldEntrySpeed);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!isWarp)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
                ActorVitalsPackets.BuildZenyChange2435(entityId, character.Gold));
            logger.LogDebug("Session {Id}: Sent gold balance {Gold} (2435)", session.SessionId, character.Gold);
        }

        logger.LogInformation("Session {Id}: World entry packets sent for '{Name}'",
            session.SessionId, character.Name);
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

 /// <summary>A skill id on the wire is the skill group times this factor plus the skill level.</summary>
    private const uint SkillIdGroupFactor = 100u;

 /// <summary>Result code of the active-pet answer when no pet is active (0x7531).</summary>
    private const ushort PCollectionNoActivePetResult = 30001;

 /// <summary>Length of the quick-slot list header counted in its length word: the u64 character uid and the u16 slot count.</summary>
    private const int QuickSlotListHeaderLength = sizeof(ulong) + sizeof(ushort);
 /// <summary>Length of one quick-slot record: page, slot, entry type, a zero byte, entry id, amount and item uid.</summary>
    private const int QuickSlotRecordLength = 20;

 /// <summary>Length of a counted list header: the u32 length word and the u16 count.</summary>
    private const int CountedListHeaderLength = sizeof(uint) + sizeof(ushort);
 /// <summary>Length of the monster-book summary: tier, maximum skill level, registered count and skill cooldown.</summary>
    private const int MonsterBookSummaryLength = sizeof(byte) + sizeof(byte) + sizeof(uint) + sizeof(uint);

 /// <summary>Length of the skill list header counted in its self length: self length, message tag, a zero u32 and the count.</summary>
    private const int UseSkillListHeaderLength = 2 + 2 + 4 + 4;



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
            var ids = skillList.Select(s => s.Group * SkillIdGroupFactor + s.Level).Distinct().Order().ToArray();
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


 /// <summary>
 /// Writes the map-load body that tells the client to load a map at a position.
 /// </summary>
    private static void WriteMapLoad(PacketWriter writer, uint entityId, uint mapId, float posX, float posY, float posZ)
    {
        var lengthPos = writer.Reserve(4); 
        var dataStart = writer.Length;

        writer.WriteUInt32(mapId);     
        writer.WriteUInt32(entityId);  

        
        writer.WriteSingle(posX);
        writer.WriteSingle(posY);
        writer.WriteSingle(posZ);
        writer.WriteSingle(0f); 

        
        writer.WriteByte(0); 
        writer.WriteByte(0); 
        writer.WriteUInt16(0); 

        writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
    }

 /// <summary>Message tag of the skill list message.</summary>
    internal const ushort MsgTagUseSkillList = 0xE3B3; 

 /// <summary>
 /// Sends the messages that precede a map-load message when the map changes: key config, dependent dungeon info, trait initialization, the trait message and the server time.
 /// </summary>
 /// <remarks>
 /// Used by the world-entry sequence and by cross-map warps.
 /// The key config carries the character's saved bindings, or the empty form when none were saved.
 /// The trait initialization message is sent before the trait message, because it carries the current HP and SP of that message.
 /// </remarks>
    internal static async Task SendPreMapLoadBurstAsync(ProudSession session, Character character, uint entityId,
        KeyConfigDocument? keyConfig, byte[] pcTrait2498)
    {
        await session.SendInternalRmiAsync(ZoneS2COpcodes.Notify_ZSCLN_DB_ACCOUNT_CONFIG, KeyConfigCodec.BuildAccountConfig2558(entityId, keyConfig));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnSendDependentDungeonInfo, BuildDependentDungeonInfo2743());
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait, TraitPackets.BuildPcInitTrait2595(character, pcTrait2498));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, pcTrait2498);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyServerTimeToLoginPC, BuildServerTimeToLoginPC2669(ServerWallClock.Now()));
    }

 /// <summary>
 /// The dependent dungeon info message for a character with no dependent dungeon progress.
 /// </summary>
    internal static byte[] BuildDependentDungeonInfo2743() => EmptyCountedList();

 /// <summary>
 /// A length-prefixed counted list with no entries: the u32 length word (2) followed by a u16 count of 0.
 /// </summary>
    private static byte[] EmptyCountedList()
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

 /// <summary>
 /// Builds the craft index message listing the first-level recipes of a profession, plus any recipes the character has learned.
 /// </summary>
 /// <remarks>
 /// Returns null when there are no recipes, since an empty message is not sent.
 /// </remarks>
    internal static byte[]? BuildNotifyCraftIndex2535(IEnumerable<RO2.GameData.Tables.CraftItemList> recipes, byte proType,
        IEnumerable<ushort>? learned = null)
    {
        var ids = CraftRecipeStore.KnownRecipeIds(recipes, proType, learned);
        if (ids.Count == 0) return null;

        using var w = new PacketWriter(CountedListHeaderLength + sizeof(ushort) * ids.Count);
        w.WriteUInt32((uint)(sizeof(ushort) + sizeof(ushort) * ids.Count)); 
        w.WriteUInt16((ushort)ids.Count);          
        foreach (var id in ids) w.WriteUInt16(id); 
        return w.ToArray();
    }

 /// <summary>
 /// Builds the skill list body sent at world entry: the learned skills, each as a four-byte id.
 /// </summary>
 /// <remarks>
 /// The length follows the number of skills, so an empty list is encoded with a zero count.
 /// </remarks>
    internal static byte[] BuildUseSkillList2488(IReadOnlyList<(uint Group, byte Level)> skills)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var selfLen = UseSkillListHeaderLength + sizeof(uint) * skills.Count;
        using var tw = new PacketWriter();
        tw.WriteCompactLength(selfLen);      
        tw.WriteUInt16((ushort)selfLen);     
        tw.WriteUInt16(MsgTagUseSkillList);  
        tw.WriteUInt32(0);                   
        tw.WriteUInt32((uint)skills.Count);  
        
        
        
        
        foreach (var (group, level) in skills.OrderBy(s => s.Group).ThenBy(s => s.Level))
            tw.WriteUInt32(group * SkillIdGroupFactor + level); 
        return tw.ToArray();
    }

 /// <summary>
 /// Sends the learned skill list, or nothing when the list is empty.
 /// </summary>
 /// <remarks>
 /// An empty list is never sent, because the client expects at least the class starter skills.
 /// </remarks>
    internal static async Task SendUseSkillList2488Async(ProudSession session, IReadOnlyList<(uint Group, byte Level)> skills)
    {
        if (skills.Count == 0) return;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnUseSkillList, BuildUseSkillList2488(skills));
        
        UseSkillListLedger.Record(session, skills);
    }

 /// <summary>
 /// Builds the monster-book summary: the tier, maximum skill level and skill cooldown for the registered monster count.
 /// </summary>
 /// <remarks>
 /// The tier row is the one whose starting count the registered count has reached.
 /// </remarks>
    internal static byte[] BuildNfyMCollection2913(int registeredCount, GameDataStore? gameData)
    {
        var tier = gameData?.CollectRate
            .Where(r => registeredCount >= r.Num_Start)
            .MaxBy(r => r.Num_Start);
        using var w = new PacketWriter(MonsterBookSummaryLength);
        w.WriteByte(tier?.Index ?? 0);
        w.WriteByte(tier?.Skill_Max ?? 0);
        w.WriteUInt32((uint)Math.Max(0, registeredCount));
        w.WriteUInt32(tier?.Skill_Cooltime ?? 0);
        return w.ToArray();
    }

    
    
    
    
    
    
    
    
}
