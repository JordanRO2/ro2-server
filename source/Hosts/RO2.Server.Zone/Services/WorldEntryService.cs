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
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds and sends the server packet sequence for entering the world after login (RMI 1417).
/// </summary>
/// <remarks>
/// The sequence includes the character detail (2310), the map load (2311), and optional game time (2321) and login key (2733) packets.
/// </remarks>
public static class WorldEntryService
{
 /// <summary>Default starting map (<see cref="SpawnRules.DefaultMapId"/>).</summary>
    public const uint DefaultMapId = SpawnRules.DefaultMapId;
 /// <summary>Default spawn X position on the default starting map.</summary>
    public const float DefaultX = SpawnRules.DefaultX;
    public const float DefaultY = SpawnRules.DefaultY;
    public const float DefaultZ = SpawnRules.DefaultZ;
    private const string ChatHost = "127.0.0.1";
    private const ushort ChatPort = 7401;

    public static float GetDefaultX() => DefaultX;
    public static float GetDefaultY() => DefaultY;
    public static float GetDefaultZ() => DefaultZ;

 /// <summary>
 /// Resolves the map and position a character enters the world at.
 /// </summary>
 /// <remarks>
 /// Delegates to <see cref="SpawnRules.ResolveSpawnPosition"/> with the game-data catalog adapter.
 /// </remarks>
    public static (uint MapId, float X, float Y, float Z) ResolveSpawnPosition(
        Character character, GameDataStore? data)
        => SpawnRules.ResolveSpawnPosition(character.MapId, character.X, character.Y, character.Z,
            data is null ? null : new GameDataWorldCatalog(data));

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

        // The login-only values (talent points, skill list) are computed on first use: only the login steps read them.
        (byte Talent, IReadOnlyList<(uint Group, byte Level)> Skills)? loginData = null;
        (byte Talent, IReadOnlyList<(uint Group, byte Level)> Skills) LoginData()
        {
            if (loginData is { } ready) return ready;
            var learnedPairs = (learnedSkills ?? (IReadOnlyList<CharacterSkill>)Array.Empty<CharacterSkill>())
                .Select(s => (s.SkillId, s.Level));
            byte talentRemaining = gameData is null
                ? (byte)Math.Clamp((character.Level - 1) - learnedPairs.Sum(p => (int)p.Level), 0, 255)
                : TalentRules.TalentPointsRemaining(gameData, character.Job, character.Level, learnedPairs);
            IReadOnlyList<(uint Group, byte Level)> loginSkills = useSkillList ?? learnedPairs.ToArray();
            loginData = (talentRemaining, loginSkills);
            return loginData.Value;
        }

        var plan = WorldEntryPlan.For(isWarp ? WorldEntryMode.Warp : WorldEntryMode.Login);
        foreach (var step in plan.AfterPreMapLoad)
        {
            var opcode = WorldEntryStepOpcodes.Of(step);
            switch (step)
            {
                case WorldEntryStep.CraftIndex:
                    if (CharacterSerializer.WireProJob(character).Id == 0
                        && gameData is not null
                        && CraftRecipeStore.BuildNotifyCraftIndex2535(gameData.CraftItemList, CharacterHeadRules.CreationProfessionByte(character), learnedCraftRecipes) is { } craftIndex)
                        await session.SendInternalRmiAsync(opcode, craftIndex);
                    break;

                case WorldEntryStep.CharacterDetail:
                {
                    var (talentRemaining, loginSkills) = LoginData();
                    var vitals = TraitPackets.VitalsOf(pcTraitEntry);
                    await session.SendRmiAsync(opcode, writer =>
                    {
                        WorldEntryPackets.WriteCharacterDetail(writer, character, charUid, entityId, inventoryItems, mapId, posX, posY, posZ, activeQuests, talentRemaining, gameData,
                            vitals: vitals, skillList: loginSkills);
                    });
                    logger.LogDebug("Session {Id}: Sent OnCharacterDetailACK (RMI 2310) ENCRYPTED", session.SessionId);
                    break;
                }

                case WorldEntryStep.LoginPcTrait:
                    await session.SendInternalRmiAsync(
                        opcode, TraitPackets.BuildPcTrait2498FromDonor(TraitPackets.PcTrait2498Donor, character, equipDerived, gameData));
                    break;

                case WorldEntryStep.UseSkillList:
                    await SendUseSkillList2488Async(session, LoginData().Skills);
                    break;

                case WorldEntryStep.CardBookCharge:
                    if (cardBookChargedUntil is { } chargedUntil)
                        await session.SendInternalRmiAsync(opcode, CardBookChargeStore.Build2793(chargedUntil, DateTime.Now));
                    break;

                case WorldEntryStep.RankingTopList:
                    await session.SendInternalRmiAsync(opcode, WorldEntryPackets.EmptyCountedList());
                    break;

                case WorldEntryStep.MapLoad:
                    await session.SendInternalRmiAsync(opcode, WorldEntryPackets.BuildMapLoad2311(entityId, mapId, posX, posY, posZ));
                    logger.LogDebug("Session {Id}: Sent OnReceiveMapLoad (INT 2311) for map {MapId}", session.SessionId, mapId);
                    break;

                case WorldEntryStep.QuickSlots:
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
                    await session.SendInternalRmiAsync(opcode, tw.GetWrittenMemory());
                    logger.LogDebug("Session {Id}: restored {Count} quick slot(s)",
                        session.SessionId, slots.Count);
                    break;
                }

                case WorldEntryStep.VehicleQuickSlot:
                    await session.SendInternalRmiAsync(opcode, VehiclePackets.BuildVehicleQuickSlot2523Empty());
                    break;

                case WorldEntryStep.AllVehicleList:
                    VehicleClientList.Record(session.SessionId, vehicles ?? Array.Empty<OwnedVehicle>());
                    await session.SendInternalRmiAsync(opcode,
                        VehiclePackets.BuildAllVehicleList2522(vehicles ?? Array.Empty<OwnedVehicle>(), VehiclePackets.NowOle()));
                    break;

                case WorldEntryStep.MonsterCollection:
                    await session.SendInternalRmiAsync(opcode,
                        CollectionPackets.BuildNfyMCollection2913(collection?.RegisteredMonsterIds.Count ?? 0, gameData));
                    break;

                case WorldEntryStep.PetCollection:
                    await session.SendInternalRmiAsync(opcode,
                        CollectionPackets.BuildNfyPCollection(collection?.RegisteredPetIds.Count ?? 0));
                    break;

                case WorldEntryStep.ActivePet:
                    await session.SendInternalRmiAsync(opcode, collection is { ActivePetId: not 0 }
                        ? CollectionPackets.BuildAnsPCollectionSetActivePet(collection.ActivePetId, collection.ActivePetFlag, 0)
                        : CollectionPackets.BuildAnsPCollectionSetActivePet(0, 0, PCollectionNoActivePetResult));
                    break;

                case WorldEntryStep.RegisterCardBook:
                    if (inventoryItems is not null && gameData is not null)
                        foreach (var cardId in CardRegistrationNotifier.HeldMvpCards(inventoryItems, gameData))
                            await session.SendRmiAsync(opcode, CardPackets.BuildRegistCardBook2588(cardId));
                    break;

                case WorldEntryStep.MoveSpeed:
                    await session.SendInternalRmiAsync(
                        opcode, TraitPackets.BuildActorMoveSpeed2497(entityId, TraitPackets.PcModalWorldEntrySpeed));
                    logger.LogDebug("Session {Id}: Sent OnChangeActorMoveSpeed (INT 2497) speed={Speed}",
                        session.SessionId, TraitPackets.PcModalWorldEntrySpeed);
                    break;

                case WorldEntryStep.ZenyBalance:
                    await session.SendInternalRmiAsync(opcode,
                        ActorVitalsPackets.BuildZenyChange2435(entityId, character.Gold));
                    logger.LogDebug("Session {Id}: Sent gold balance {Gold} (2435)", session.SessionId, character.Gold);
                    break;

                default:
                    throw new InvalidOperationException($"world-entry step {step} is not a step of the entry burst");
            }
        }

        logger.LogInformation("Session {Id}: World entry packets sent for '{Name}'",
            session.SessionId, character.Name);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
 /// <summary>Result code of the active-pet answer when no pet is active (0x7531).</summary>
    private const ushort PCollectionNoActivePetResult = 30001;

 /// <summary>Length of the quick-slot list header counted in its length word: the u64 character uid and the u16 slot count.</summary>
    private const int QuickSlotListHeaderLength = sizeof(ulong) + sizeof(ushort);
 /// <summary>Length of one quick-slot record: page, slot, entry type, a zero byte, entry id, amount and item uid.</summary>
    private const int QuickSlotRecordLength = 20;

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
        foreach (var step in WorldEntryPlan.PreMapLoad)
        {
            var opcode = WorldEntryStepOpcodes.Of(step);
            switch (step)
            {
                case WorldEntryStep.AccountConfig:
                    await session.SendInternalRmiAsync(opcode, KeyConfigCodec.BuildAccountConfig2558(entityId, keyConfig));
                    break;
                case WorldEntryStep.DependentDungeonInfo:
                    await session.SendInternalRmiAsync(opcode, DungeonFinderPackets.BuildDependentDungeonInfo2743());
                    break;
                case WorldEntryStep.PcInitTrait:
                    await session.SendInternalRmiAsync(opcode, TraitPackets.BuildPcInitTrait2595(character, pcTrait2498));
                    break;
                case WorldEntryStep.PcTrait:
                    await session.SendInternalRmiAsync(opcode, pcTrait2498);
                    break;
                case WorldEntryStep.ServerTime:
                    await session.SendInternalRmiAsync(opcode, WorldEntryPackets.BuildServerTimeToLoginPC2669(ServerWallClock.Now()));
                    break;
                default:
                    throw new InvalidOperationException($"world-entry step {step} is not a pre-map-load step");
            }
        }
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
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnUseSkillList, SkillPackets.BuildUseSkillList2488(skills));
        
        UseSkillListLedger.Record(session, skills);
    }

}
