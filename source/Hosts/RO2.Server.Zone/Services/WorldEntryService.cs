using System.Text;
using Microsoft.Extensions.Logging;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Inventory.Domain;

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
        IReadOnlyList<RO2.Domain.Entities.CharacterQuickSlot>? quickSlots = null,
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

        
        
        
        
        
        
        
        
        
        StatService.EquipStats? equipDerived = gameData is not null && inventoryItems is not null
            ? StatService.ComputeEquipmentStats(gameData, inventoryItems)
            : null;

        
        
        
        byte[] pcTraitEntry;
        {
            
            
            
            
            
            
            
            
            pcTraitEntry = BuildPcTrait2498FromDonor(PcTrait2498Donor, character, equipDerived, gameData);
        }
        
        
        
        
        
        
        
        if (isWarp && warpPcTrait2498 is { Length: TraitPackets.PcTraitBodyLen })
            pcTraitEntry = warpPcTrait2498;

        
        await SendPreMapLoadBurstAsync(session, character, entityId, keyConfig, pcTraitEntry);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!isWarp && CharacterSerializer.WireProJob(character).Id == 0
            && gameData is not null
            && BuildNotifyCraftIndex2535(gameData.CraftItemList, CreationProfessionByte(character), learnedCraftRecipes) is { } craftIndex)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCraftIndex, craftIndex);

        
        
        
        
        
        
        
        var learnedPairs = (learnedSkills ?? (IReadOnlyList<CharacterSkill>)Array.Empty<CharacterSkill>())
            .Select(s => (s.SkillId, s.Level));
        byte talentRemaining = gameData is null
            ? (byte)Math.Clamp((character.Level - 1) - learnedPairs.Sum(p => (int)p.Level), 0, 255)
            : StatService.TalentPointsRemaining(gameData, character.Job, character.Level, learnedPairs);

        
        
        
        
        
        IReadOnlyList<(uint Group, byte Level)> loginSkills = useSkillList ?? learnedPairs.ToArray();

        if (!isWarp)
        {
            
            
            var vitals = VitalsOf(pcTraitEntry);
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
                ZoneS2COpcodes.OnPcTrait, BuildPcTrait2498FromDonor(PcTrait2498Donor, character, equipDerived, gameData));
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

        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveMapLoad, BuildMapLoad2311(entityId, mapId, posX, posY, posZ));
        logger.LogDebug("Session {Id}: Sent OnReceiveMapLoad (INT 2311) for map {MapId}", session.SessionId, mapId);

        
        
        
        
        
        
        
        
        
        
        
        
        
        {
            var slots = quickSlots ?? Array.Empty<RO2.Domain.Entities.CharacterQuickSlot>();
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

 /// <summary>Combat class (job id) sent for a character that has none.</summary>
    private const byte NoClassFallbackJob = 0x01;

 /// <summary>Offset of the combat class picked at creation, inside the stored 23-byte creation block. The 2310 head carries it at <see cref="HeadOffset.CombatClass"/>.</summary>
    private const int CreateClassOffset = 19;
 /// <summary>Offset of the profession picked at creation, inside the stored 23-byte creation block. The 2310 head carries it at <see cref="HeadOffset.CreationProfession"/>.</summary>
    private const int CreateProfessionOffset = 21;

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

 /// <summary>Facing byte of the 2310 head: one step is this many degrees.</summary>
    private const double FacingStepDegrees = 2.0;
 /// <summary>Facing byte of the 2310 head: the number of steps in a full turn.</summary>
    private const int FacingStepCount = 180;

 /// <summary>Largest stack count an item record carries.</summary>
    private const int MaxRecordStackCount = 255;
 /// <summary>Length of the grinding statistics block of an item record: 5 values of 4 bytes.</summary>
    private const int GrindingStatsLength = 5 * 4;

 /// <summary>Storage type of a worn item.</summary>
    private const byte StorageWorn = 1;
 /// <summary>Storage type of an item in the bank.</summary>
    private const byte StorageBank = 2;
 /// <summary>Storage type of a bag worn in the character bag window.</summary>
    private const byte StorageEquippedBag = 3;

 /// <summary>Bag id of worn gear; its slots are equipment slots.</summary>
    private const byte BagWorn = 0;
 /// <summary>Bag id of the main inventory bag and the character bag window.</summary>
    private const byte BagMain = 1;
 /// <summary>Bag id of the first extra bag; extra bags run up to <see cref="BagLastExtra"/>.</summary>
    private const int BagFirstExtra = 2;
 /// <summary>Bag id of the last extra bag.</summary>
    private const int BagLastExtra = 4;
 /// <summary>Bag id of the bank.</summary>
    private const byte BagBank = 5;
 /// <summary>Highest equipment slot of worn gear (slots 0 to 49).</summary>
    private const int EquipSlotMax = 49;
 /// <summary>First slot of the main inventory bag (slots 50 to 69).</summary>
    private const int MainBagFirstSlot = 50;
 /// <summary>Number of items the main inventory bag holds before items overflow into the extra bags.</summary>
    private const int MainBagSlotCount = 20;
 /// <summary>Number of slots of each extra bag (slots 0 to 34).</summary>
    private const int ExtraBagSlotCount = 35;
 /// <summary>First slot of the character bag window, which holds the worn bags (slots 70 to 72).</summary>
    private const int CharacterBagFirstSlot = 70;
 /// <summary>Number of slots of the character bag window.</summary>
    private const int CharacterBagSlotCount = 3;

 /// <summary>Item bind type: bound when equipped.</summary>
    private const byte BindOnEquip = 1;
 /// <summary>Item bind type: bound when acquired.</summary>
    private const byte BindOnAcquire = 2;

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

 /// <summary>Fixed first byte of the map-load body.</summary>
    private const byte MapLoadLeadByte = 0x01;
 /// <summary>Fixed u16 at offset 1 of the map-load body.</summary>
    private const ushort MapLoadMarkerA = 0x2B2B;
 /// <summary>Fixed u16 at offset 4 of the map-load body.</summary>
    private const ushort MapLoadMarkerB = 0xE294;
 /// <summary>Last byte of the map-load body (offset 44). This value is known to spawn the client; the byte is not otherwise identified.</summary>
    private const byte MapLoadTrailerByte = 0xAB;

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
            else WriteItemInstance(writer, item);
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
        writer.WriteByte(CombatJobByte(character));  // +78 combat class (job id)
        writer.WriteUInt16(0);  // +79 zeros

        
        
        
        
        
        
        
        
        writer.WriteUInt32(vitals?.CurHp ?? DefaultHp);  // +81 current HP
        writer.WriteUInt32(vitals is { } vs ? vs.CurSp : DefaultSp);  // +85 current SP (u32 holding the u16 value)

        
        writer.WriteSingle(posX);  // +89 position X
        writer.WriteSingle(posY);  // +93 position Y
        writer.WriteSingle(posZ);  // +97 position Z

        
        writer.WriteUInt32(mapId);  // +101 map id

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        writer.WriteUInt16(character.BindIndex);  // +105 save-point bind index (0 = none)
        
        writer.WriteByte(FacingByte(character.FacingYaw));  // +107 facing, 2-degree steps
        writer.WriteZeros(4);  // +108 zeros
        writer.WriteUInt64((ulong)Math.Max(0, character.Gold));  // +112 zeny wallet
        writer.WriteByte(HeadConstByte120);  // +120 fixed byte
        
        
        
        
        writer.WriteByte(BankExpansion.SlotsOf(character));  // +121 bank slot count
        writer.WriteUInt32(HeadConstField122);  // +122 fixed u32

        
        
        
        
        
        
        
        
        writer.WriteUInt32(KharaService.PublishedPoints(character.Id));  // +126 Khara point balance
        
        
        
        WriteTentativeJobBlock(writer, character);  // +130 profession, job level, job exp, equip-hide flags

        
        
        
        
        
        
        writer.WriteByte(talentPointsRemaining);  // +142 remaining talent points
        
        
        
        
        
        
        
        KharaTitleStore.WriteHeadTitleA(writer, character.Id, DateTime.UtcNow);  // +143 title A timer and title A, then 10 bytes filled below

        
        
        
        
        
        
        
        var (bonus5, unspentBonusPoints) = BonusStatBlock(character);
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
        
        
        
        
        
        
        
        
        writer.WriteUInt64(JobMasteryPoints(character));  // +191 job-mastery point total
        
        
        
        
        
        writer.WriteZeros(HeadReservedBytes);  // +199 reserved

        var blobWritten = writer.Length - blobStart;
        if (blobWritten != HeadOffset.Length)
            throw new InvalidOperationException($"2310 base blob expected {HeadOffset.Length} bytes, wrote {blobWritten}");
    }

 /// <summary>
 /// Converts a facing yaw in radians to the facing byte of the 2310 head: 0 to 179 in 2-degree steps. A non-finite yaw gives 0.
 /// </summary>
    internal static byte FacingByte(float yawRadians)
    {
        if (!float.IsFinite(yawRadians))
            return 0;
        double deg = yawRadians * (180.0 / Math.PI) % 360.0;
        if (deg < 0) deg += 360.0;
        return (byte)((int)Math.Round(deg / FacingStepDegrees, MidpointRounding.AwayFromZero) % FacingStepCount);
    }

 /// <summary>
 /// The allocated bonus STR, AGI, INT, WIS and VIT, and the unspent bonus points. Shared by the stat sheet and the character head so they agree.
 /// </summary>
    internal static (ushort[] Bonus5, ushort Unspent) BonusStatBlock(Character character)
    {
        int earned = StatService.BonusStatPointsEarned(character.Level);
        int spent = character.BonusStr + character.BonusAgi + character.BonusInt
                  + character.BonusWis + character.BonusVit + character.BonusLuk;
        var unspent = (ushort)Math.Clamp(earned - spent, 0, ushort.MaxValue);
        var bonus5 = new ushort[]
        {
            (ushort)Math.Clamp(character.BonusStr, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusAgi, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusInt, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusWis, 0, ushort.MaxValue),
            (ushort)Math.Clamp(character.BonusVit, 0, ushort.MaxValue),
        };
        return (bonus5, unspent);
    }

 /// <summary>
 /// The character's job-mastery point total. Always 0, because the server does not store mastery points yet.
 /// </summary>
 /// <remarks>
 /// When mastery points are persisted, return them here. The total is shared with the job-mastery point change message, so both must read it from this method.
 /// </remarks>
    internal static ulong JobMasteryPoints(Character? character) => 0;

 /// <summary>
 /// Writes the base experience field of the character head. Stays zero until the character's experience is stored.
 /// </summary>
    private static void WriteTentativeBaseExp(PacketWriter writer, Character character)
    {
        writer.WriteUInt64(character.Exp);          
    }

 /// <summary>
 /// Returns the combat class of the character: its job id, as the client reads it for the class name and job requirements.
 /// </summary>
 /// <remarks>
 /// Before any job change this is the class chosen at creation. After a job change it is the new class stored on the character.
 /// A zero is never sent. A character with no class sends the previous default.
 /// </remarks>
    internal static byte CombatJobByte(Character character)
    {
        var create = CreateBlock(character);
        if (create != null && create[CreateClassOffset] != 0 && character.Job == create[CreateProfessionOffset])
            return create[CreateClassOffset];
        return character.Job != 0 ? character.Job : NoClassFallbackJob;
    }

 /// <summary>
 /// Corrects the job of a character row stored before the creation class was kept separately.
 /// </summary>
 /// <remarks>
 /// Changes the stored job only when it still equals the creation profession and the stored creation class differs from it.
 /// Returns true when the row changed, so the caller can persist it. Runs at most once per row.
 /// </remarks>
    internal static bool TryHealStoredCombatClass(Character character)
    {
        var create = CreateBlock(character);
        if (create is null) return false;
        byte cls = create[CreateClassOffset], profession = create[CreateProfessionOffset];
        if (cls == 0 || cls == profession || character.Job != profession) return false;
        character.Job = cls;
        return true;
    }

 /// <summary>
 /// Returns the profession the character picked at creation, which the client reads from the character head.
 /// </summary>
 /// <remarks>
 /// A job change does not alter it. The value is read from the stored creation block, or from the character's job when there is none.
 /// </remarks>
    internal static byte CreationProfessionByte(Character character)
    {
        var create = CreateBlock(character);
        return create != null && create[CreateProfessionOffset] != 0 ? create[CreateProfessionOffset] : character.Job;
    }

 /// <summary>
 /// Sets the character's creation profession in the stored creation block. Returns the previous appearance data so a caller can restore it if its save fails.
 /// </summary>
 /// <remarks>
 /// Call <see cref="TryHealStoredCombatClass"/> first, so that the class is not read from the profession field of an older row.
 /// A stored block shorter than 23 bytes is padded with zeros.
 /// </remarks>
    internal static string? SetCreationProfession(Character character, byte profession)
    {
        var previous = character.AppearanceJson;
        var ap = CharacterSerializer.ParseAppearancePublic(previous);
        var raw = ap.RawBytes ?? [];
        if (raw.Length <= CreateProfessionOffset)
        {
            var padded = new byte[CreateBlockLength];
            Buffer.BlockCopy(raw, 0, padded, 0, raw.Length);
            raw = padded;
        }
        else
        {
            raw = (byte[])raw.Clone();
        }
        raw[CreateProfessionOffset] = profession;
        ap.RawBytes = raw;
        ap.BaseClass = profession;   
        character.AppearanceJson = CharacterSerializer.ToJson(ap);
        return previous;
    }

 /// <summary>Length of the creation block that is kept verbatim from character creation.</summary>
    private const int CreateBlockLength = 23;

 /// <summary>
 /// The stored creation block when it holds the profession byte, otherwise null. Rows made by character creation always carry all 23 bytes.
 /// </summary>
    private static byte[]? CreateBlock(Character character)
    {
        var raw = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson).RawBytes;
        return raw is { Length: > CreateProfessionOffset } ? raw : null;
    }

 /// <summary>
 /// Writes the job block of the character head: profession, job level, job experience and the equipment-hide flags.
 /// </summary>
 /// <remarks>
 /// The caller writes the four-byte field that precedes this block.
 /// </remarks>
    private static void WriteTentativeJobBlock(PacketWriter writer, Character character)
    {
        writer.WriteByte(CreationProfessionByte(character));  // +130 profession picked at creation
        writer.WriteByte(character.JobLevel);  // +131 job level
        writer.WriteUInt64(character.JobExp);  // +132 job experience
        
        
        
        
        writer.WriteUInt16(character.EquipHideFlag);  // +140 equipment-hide flags
    }

 /// <summary>
 /// Builds one 126-byte item record, for messages that carry a single item rather than a list.
 /// </summary>
 /// <remarks>
 /// Shares the inventory item writer so that every message encodes items the same way. Only the uid, item id and durability offsets are confirmed; the rest is the current model of the record.
 /// </remarks>
    internal static byte[] BuildItemRecord126(RO2.Inventory.Domain.InventoryItem item)
    {
        using var w = new PacketWriter(ItemPackets.ItemRecordLength);
        WriteItemInstance(w, item);
        return w.GetWrittenSpan().ToArray();
    }

 /// <summary>
 /// Writes a single 126-byte item instance, in the same format as the item information message.
 /// </summary>
    private static void WriteItemInstance(PacketWriter writer, RO2.Inventory.Domain.InventoryItem item)
    {
        var startPos = writer.Length;

        writer.WriteUInt64(item.Id);  // +0x00 item uid
        writer.WriteUInt32(item.ItemId);  // +0x08 template id
        
        
        
        
        
        
        writer.WriteUInt16(item.Durability >= 0 ? (ushort)item.Durability : UnsetRecordDurability(item.ItemId));  // +0x0C durability
        
        
        
        
        writer.WriteByte((byte)Math.Clamp(item.Count <= 0 ? 1 : item.Count, 1, MaxRecordStackCount));  // +0x0E stack count, at least 1
        
        
        
        
        
        
        byte bagIx, slotIx;
        if (item.StorageType == StorageWorn)
        {
            // Worn gear: bag 0, the slot is the equipment slot.
            
            
            
            
            
            
            bagIx = BagWorn;
            slotIx = (byte)Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, EquipSlotMax);
        }
        else if (item.StorageType == StorageBank)
        {
            // Bank: its own bag id, slots run up to the largest bank.
            
            
            
            
            bagIx = BagBank;
            slotIx = (byte)Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, BankExpansion.MaxBankSlots - 1);
        }
        else if (item.StorageType == StorageEquippedBag)
        {
            // Worn bag: shown in the character bag window, slots 70 to 72 of the main bag id.
            
            
            
            
            bagIx = BagMain;
            slotIx = (byte)(CharacterBagFirstSlot + Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, CharacterBagSlotCount - 1));
        }
        else
        {
            
            var flat = item.Slot < 0 ? 0 : (int)item.Slot;
            if (flat < MainBagSlotCount) { bagIx = BagMain; slotIx = (byte)(MainBagFirstSlot + flat); }
            else { var i = flat - MainBagSlotCount; bagIx = (byte)Math.Clamp(BagFirstExtra + i / ExtraBagSlotCount, BagFirstExtra, BagLastExtra); slotIx = (byte)(i % ExtraBagSlotCount); }
        }
        writer.WriteByte(bagIx);  // +0x0F bag
        writer.WriteByte(slotIx);  // +0x10 slot
        
        
        
        
        
        writer.WriteUInt16(item.EnchantReinforceId);  // +0x11 timed enchant id
        writer.WriteUInt16(item.EnchantSeconds);  // +0x13 timed enchant seconds left
        writer.WriteZeros(2);  // +0x15 not modelled
        
        writer.WriteUInt32(item.DyeColor);  // +0x17 dye colour of slot 0
        writer.WriteByte(RecordBindFlags(item));  // +0x1B bind flags
        writer.WriteByte(0);  // +0x1C zero
        writer.WriteByte(item.Refine);  // +0x1D refine level
        writer.WriteZeros(3);  // +0x1E not modelled
        WriteRuneholes(writer, item);  // +0x21 rune holes, 5 x 7 bytes
        
        
        
        writer.WriteByte(item.Element);  // +0x44 bestowed element
        
        
        
        
        
        writer.WriteUInt32(RecordAbilityValue(item));  // +0x45 ability value
        writer.WriteUInt16(0);  // +0x49 card set id
        writer.WriteZeros(GrindingStatsLength);  // +0x4B grinding stats
        writer.WriteByte(0);  // +0x5F accessory trait
        writer.WriteByte(0);  // +0x60 accessory trait sub
        writer.WriteSingle(0f);  // +0x61 accessory value
        
        
        
        
        writer.WriteUInt32(item.DyeColor2);  // +0x65 dye colour of slot 1
        writer.WriteUInt32(item.DyeColor3);  // +0x69 dye colour of slot 2
        writer.WriteUInt32(0);  // +0x6D outfit look template, not modelled
        
        
        writer.WriteUInt32(item.FxEnchantIndex);  // +0x71 FX enchant index

        
        var written = writer.Length - startPos;
        if (written < ItemPackets.ItemRecordLength)
            writer.WriteZeros(ItemPackets.ItemRecordLength - written);
    }

 /// <summary>Bind flag bit of an item record, set when the item is bound to its owner.</summary>
    internal const byte RecordBoundFlag = 0x02;

 /// <summary>
 /// The bind flags of an item record: the bound flag when the item is bound by its template or by having been worn; otherwise 0.
 /// </summary>
 /// <remarks>
 /// The flags are read from the template's bind type and the item's storage location and history.
 /// The template comes from the loaded game data. Without loaded game data the value stays 0.
 /// </remarks>
    internal static byte RecordBindFlags(RO2.Inventory.Domain.InventoryItem item)
        => GameDataStore.Loaded?.GetItem(item.ItemId) is { } template
            ? RecordBindFlags(template.Bind_Type, item.StorageType, item.Bound)
            : (byte)0;

 /// <summary>
 /// The bind flags of an item record, given its bind type, storage type and whether it was ever worn.
 /// </summary>
 /// <param name="bindType">Bind type of the item template.</param>
 /// <param name="storageType">Storage location of the item: 1 for worn, 3 for an equipped bag.</param>
 /// <param name="everWorn">Whether the item was worn at some point.</param>
    internal static byte RecordBindFlags(byte bindType, byte storageType, bool everWorn = false)
        => bindType == BindOnAcquire || (bindType == BindOnEquip && (storageType is StorageWorn or StorageEquippedBag || everWorn)) ? RecordBoundFlag : (byte)0;

 /// <summary>
 /// The ability value of an item record: the item's own value when it has one, otherwise the template's value.
 /// </summary>
 /// <remarks>
 /// Items bought from an NPC stack store 0 for their own value, which takes precedence over the template value.
 /// Returns 0 when no loaded game data knows the template.
 /// </remarks>
    internal static uint RecordAbilityValue(RO2.Inventory.Domain.InventoryItem item)
        => RecordAbilityValue(item.ItemId, item.AbilityValue);

 /// <summary>
 /// The ability value of an item record for a template and an optional instance value.
 /// </summary>
 /// <param name="templateId">Item template id.</param>
 /// <param name="instanceValue">The item's own value, or null to use the template's value.</param>
    internal static uint RecordAbilityValue(uint templateId, uint? instanceValue)
        => instanceValue ?? (GameDataStore.Loaded?.GetItem(templateId) is { } template ? (uint)Math.Max(0, template.Abili_val) : 0u);

 /// <summary>
 /// Durability of an item record whose durability is unset: the template's durability, or the default maximum when the template is unknown.
 /// </summary>
    private static ushort UnsetRecordDurability(uint templateId)
        => GameDataStore.Loaded?.GetItem(templateId) is { } template
            ? (ushort)Math.Min(template.Durability, ushort.MaxValue)
            : ItemPackets.DurabilityMax;

 /// <summary>
 /// Writes the 35-byte runehole block of an item record from the item's opened holes.
 /// </summary>
 /// <remarks>
 /// Every item record carries this block, so later records of an item keep its holes.
 /// Before the game data is loaded the block stays zero.
 /// </remarks>
    private static void WriteRuneholes(PacketWriter writer, RO2.Inventory.Domain.InventoryItem item)
    {
        const int blockLength = ItemWorkbench.HoleCount * 7;
        var store = GameDataStore.Loaded;
        if (store is null || string.IsNullOrWhiteSpace(item.CardsJson))
        {
            writer.WriteZeros(blockLength);
            return;
        }
        var record = new byte[ItemPackets.ItemRecordLength];
        ItemWorkbench.ApplyRuneholes(record, item, store);
        writer.WriteBytes(record.AsSpan(ItemWorkbench.RuneholeOffset, blockLength));
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
    private static readonly byte[] PcTrait2498Donor = BuildPcTrait2498Default();

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
        StatService.EquipStats? derived = gameData is not null && equipped is not null
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
        StatService.EquipStats? equip = null, GameDataStore? store = null, (int Hp, int Sp)? liveVitals = null)
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

        
        
        
        
        
        
        
        var (bonus5, unspentStatPoints) = BonusStatBlock(character);

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
        StatService.EquipStats? equip)
        => RegenAmounts(store is not null && store.PcBaseStatus.TryGetValue(job, out var row) ? row : null, vit, wis, equip);

 /// <summary>Regeneration amounts for the given base stat row. Null for both when the row is missing.</summary>
    internal static (ushort? Hp, ushort? Sp) RegenAmounts(RO2.Domain.GameData.Tables.PCBaseStatus? pc, int vit, int wis,
        StatService.EquipStats? equip)
    {
        if (pc is null) return (null, null);
        static decimal Coef(float c) => decimal.Round((decimal)(double)c, RegenCoefficientDecimals, MidpointRounding.ToEven);
        long hp = (long)decimal.Floor(Math.Max(0, vit) * Coef(pc.AutoHpRestorePerVit)) + pc.BaseHpRestoreAdd + (equip?.FlatOf(TraitHpRestore) ?? 0);
        long sp = (long)decimal.Floor(Math.Max(0, wis) * Coef(pc.AutoSpRestorePerWis)) + pc.BaseSpRestoreAdd + (equip?.FlatOf(TraitSpRestore) ?? 0);
        return ((ushort)Math.Clamp(hp, 0, ushort.MaxValue), (ushort)Math.Clamp(sp, 0, ushort.MaxValue));
    }

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
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait, BuildPcInitTrait2595(character, pcTrait2498));
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
    internal static byte[]? BuildNotifyCraftIndex2535(IEnumerable<RO2.Domain.GameData.Tables.CraftItemList> recipes, byte proType,
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

 /// <summary>Body length of the map-load message, in bytes.</summary>
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
