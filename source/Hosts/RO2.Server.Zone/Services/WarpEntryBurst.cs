using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-driven map move: to another map, or in place on the current map.
/// </summary>
/// <remarks>
/// A move to another map sends the departure frames, then the world-entry burst in warp mode.
/// An in-place move sends the self despawn, the map-load trigger, the quick-slot bar, and the vehicle lists.
/// In both forms the client's 1304 reply from <c>AnsMapLoadingHandler</c> sends the destination population.
/// The data is loaded by <see cref="PrepareAsync"/> before anything is sent, so a missing character row sends nothing.
/// </remarks>
public static class WarpEntryBurst
{
 /// <summary>Data read by <see cref="SendAsync"/> and <see cref="SendInPlaceAsync"/>, loaded by <see cref="PrepareAsync"/>.</summary>
    public sealed class Loaded
    {
        internal Loaded(Character character, IReadOnlyList<CharacterQuickSlot> quickSlots, CollectionDocument collection,
            KeyConfigDocument? keyConfig, IReadOnlyList<OwnedVehicle>? vehicles, IReadOnlyList<InventoryItem> equipped,
            IReadOnlyList<InventoryItem> bag)
        {
            Character = character;
            QuickSlots = quickSlots;
            Collection = collection;
            KeyConfig = keyConfig;
            Vehicles = vehicles;
            Equipped = equipped;
            Bag = bag;
        }

 /// <summary>The world-entry snapshot, or the stored character row when there is none.</summary>
        public Character Character { get; }

 /// <summary>The stored quick-slot bar.</summary>
        public IReadOnlyList<CharacterQuickSlot> QuickSlots { get; }

 /// <summary>Monster book, pet book and active pet.</summary>
        public CollectionDocument Collection { get; }

 /// <summary>Saved key bindings. Null sends the no-config form.</summary>
        public KeyConfigDocument? KeyConfig { get; }

 /// <summary>Owned vehicles. Null sends the empty list.</summary>
        public IReadOnlyList<OwnedVehicle>? Vehicles { get; }

 /// <summary>Worn gear.</summary>
        public IReadOnlyList<InventoryItem> Equipped { get; }

 /// <summary>The character's items, used for the world-entry notices of held cards.</summary>
        public IReadOnlyList<InventoryItem> Bag { get; }
    }

 /// <summary>
 /// Loads everything the departure and the burst need. Returns null, with nothing sent, when the character row is missing.
 /// </summary>
 /// <remarks>
 /// Key bindings and owned vehicles fall back to their empty forms on a storage error. Other storage errors propagate, still before anything is sent.
 /// </remarks>
    public static async Task<Loaded?> PrepareAsync(PlayerState player, IServiceProvider services, ILogger logger,
        CancellationToken ct)
    {
        var character = player.Character
            ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
        if (character is null)
            return null;

        var quickSlots = await services.GetRequiredService<IQuickSlotRepository>().GetByCharacterAsync(player.CharacterId);
        
        var collection = await CollectionService.From(services, logger).GetStateAsync(player.CharacterId, ct);

        KeyConfigDocument? keyConfig = null;
        try
        {
            keyConfig = await services.GetRequiredService<RO2.Characters.Domain.IKeyConfigRepository>()
                .GetAsync(player.CharacterId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Name} warp: could not load key config", player.Name);
        }

        IReadOnlyList<OwnedVehicle>? vehicles = null;
        try
        {
            vehicles = await services.GetRequiredService<IVehicleRepository>()
                .GetAsync(player.CharacterId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Name} warp: could not load owned vehicles", player.Name);
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var equipped = await inventory.GetEquipmentAsync(player.CharacterId);
        var bag = await inventory.GetByCharacterAsync(player.CharacterId);

        return new Loaded(character, quickSlots, collection, keyConfig, vehicles, equipped, bag);
    }

 /// <summary>
 /// The two departure frames, in order: the departure marker (2938) and the self despawn (2505) listing the mover as its own subject.
 /// </summary>
    internal static IReadOnlyList<(ushort Opcode, byte[] Body)> DepartureFrames(uint selfHandle)
    {
        var frames = new List<(ushort Opcode, byte[] Body)>();
        foreach (var step in WorldEntryPlan.Departure)
            frames.Add((WorldEntryStepOpcodes.Of(step), step switch
            {
                WorldEntryStep.DepartureMarker => SessionExitPackets.Ack2938,
                WorldEntryStep.SelfDespawn => PlayerSpawnService.BuildActorOutList(selfHandle, new[] { selfHandle }),
                _ => throw new InvalidOperationException($"world-entry step {step} is not a departure step"),
            }));
        return frames;
    }

 /// <summary>
 /// Sends the departure and the warp burst to the given map and position, then moves the player there.
 /// The caller checks that the map exists and differs from the current map.
 /// </summary>
    public static async Task SendAsync(ProudSession session, PlayerState player, Loaded loaded,
        uint mapId, float x, float y, float z, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var effects = services.GetRequiredService<EffectService>();

        
        
        
        
        
        
        await WarpDeparture.DropWarpEffectsAsync(player, store, effects);

        
        
        
        
        await RidingService.DropAsync(session, player, services);
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, logger, ct);

        
        await services.GetRequiredService<PlayerSpawnService>().NotifyPlayerDespawnAsync(player);

        foreach (var (opcode, body) in DepartureFrames(player.EntityId))
            await session.SendInternalRmiAsync(opcode, body, ct);

        
        
        
        
        
        
        var character = loaded.Character;
        AnsMapLoadingHandler.SyncLiveLevel(player, character);
        await WarpDeparture.SendKeptEffectLinesAsync(session, player, store, effects, character, loaded.Equipped);

        
        player.ClearRevivalOffers();
        player.MapId = mapId; player.X = x; player.Y = y; player.Z = z;
        
        player.MapLoaded = false;

        
        character.Gold = player.Gold;
        var sheet = effects.BuildInWorldPcTrait(player, character, loaded.Equipped);

        await WorldEntryService.SendWorldEntryAsync(
            session, character, player.CharacterUid, player.EntityId, logger,
            inventoryItems: loaded.Bag, gameData: store,
            quickSlots: loaded.QuickSlots, isWarp: true, warpSpawn: (mapId, x, y, z),
            collection: loaded.Collection, keyConfig: loaded.KeyConfig, warpPcTrait2498: sheet,
            vehicles: loaded.Vehicles);
    }

 /// <summary>
 /// The in-place map-load frames, in order: the map-load trigger, the quick-slot bar, an empty vehicle quick-slot list, and the owned vehicles.
 /// </summary>
    internal static IReadOnlyList<(ushort Opcode, byte[] Body)> InPlaceMapLoadFrames(uint selfHandle, uint mapId,
        float x, float y, float z, ulong charUid, IReadOnlyList<CharacterQuickSlot> quickSlots,
        IReadOnlyList<OwnedVehicle> vehicles, double nowOle)
    {
        var frames = new List<(ushort Opcode, byte[] Body)>();
        foreach (var step in WorldEntryPlan.InPlaceMapLoad)
            frames.Add((WorldEntryStepOpcodes.Of(step), step switch
            {
                WorldEntryStep.MapLoad => WorldEntryPackets.BuildMapLoad2311(selfHandle, mapId, x, y, z),
                WorldEntryStep.QuickSlots => GuildSkillBarRefresh.BuildQuickSlotSaveData2472(charUid, quickSlots),
                WorldEntryStep.VehicleQuickSlot => VehiclePackets.BuildVehicleQuickSlot2523Empty(),
                WorldEntryStep.AllVehicleList => VehiclePackets.BuildAllVehicleList2522(vehicles, nowOle),
                _ => throw new InvalidOperationException($"world-entry step {step} is not an in-place map-load step"),
            }));
        return frames;
    }

 /// <summary>
 /// Moves the player to a position on its own map without the departure sequence or the cross-map burst.
 /// The caller checks that the current map exists.
 /// </summary>
 /// <remarks>
 /// The ride is dropped, revival offers are cleared, other players on the map receive the self despawn, and any Illusion Pyramid is closed before the map-load trigger.
 /// The position is committed after the burst.
 /// </remarks>
    public static async Task SendInPlaceAsync(ProudSession session, PlayerState player, Loaded loaded,
        float x, float y, float z, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        await RidingService.DropAsync(session, player, services);
        
        player.ClearRevivalOffers();

        await services.GetRequiredService<PlayerSpawnService>().NotifyPlayerDespawnAsync(player);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
            PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }), ct);

        await IllusionPyramidService.CloseOnMapLoadAsync(session, player, services, logger, ct);

        player.MapLoaded = false;
        var vehicles = loaded.Vehicles ?? Array.Empty<OwnedVehicle>();
        
        VehicleClientList.Record(session.SessionId, vehicles);
        foreach (var (opcode, body) in InPlaceMapLoadFrames(player.EntityId, player.MapId, x, y, z,
                     player.CharacterUid, loaded.QuickSlots, vehicles, VehiclePackets.NowOle()))
            await session.SendInternalRmiAsync(opcode, body, ct);

        player.X = x; player.Y = y; player.Z = z;
    }
}
