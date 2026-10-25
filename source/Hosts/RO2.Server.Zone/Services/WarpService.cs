using RO2.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Map-change routine that relocates a player to another map and makes the client load it.
/// </summary>
/// <remarks>
/// Re-emits the map-load tail of the world-entry sequence, ending with the 2311 map-load trigger, so first login and an in-game warp share one path.
/// Used by every travel trigger: flights, dungeon entry, portals and GM teleport.
/// It does not resend the character detail packet, since re-running it on a live actor breaks the client.
/// The destination population completes when the client acknowledges with 1304 AnsMapLoading.
/// </remarks>
public static class WarpService
{
 /// <summary>
 /// Completes a client-driven map change (glider or flight) by moving the player server-side only. The client loads the destination itself.
 /// </summary>
 /// <remarks>
 /// Entry finishes when the client sends 1304 AnsMapLoading. Sending the full <see cref="WarpAsync"/> burst here would load the map twice.
 /// </remarks>
 /// <returns>False without moving the player when the destination map is unknown.</returns>
    public static async Task<bool> CommitClientMapChangeAsync(ProudSession session, PlayerState player,
        uint mapId, float x, float y, float z, IServiceProvider services)
    {
        var data = services.GetRequiredService<GameDataStore>();
        if (data.GetMap(mapId) is null) return false;

        var spawnSvc = services.GetRequiredService<PlayerSpawnService>();
        await spawnSvc.NotifyPlayerDespawnAsync(player);   
        
        player.ClearRevivalOffers();
        player.MapId = mapId; player.X = x; player.Y = y; player.Z = z;
        
        
        return true;
    }

 /// <summary>
 /// Warps a player to a position on another map.
 /// </summary>
 /// <remarks>
 /// A zero x, y and z use the map's default entry point. Returns false without moving the player when the destination map is unknown.
 /// NPCs for the destination are sent after the client's 1304 AnsMapLoading, not before the map-load packet.
 /// </remarks>
    public static async Task<bool> WarpAsync(ProudSession session, PlayerState player, uint mapId,
        float x, float y, float z, IServiceProvider services)
    {
        var data = services.GetRequiredService<GameDataStore>();
        var mapData = data.GetMap(mapId);
        if (mapData is null) return false;

        
        if (x == 0f && y == 0f && z == 0f && (mapData.x != 0f || mapData.y != 0f || mapData.z != 0f))
        {
            x = mapData.x; y = mapData.y; z = mapData.z;
        }

        var spawnSvc    = services.GetRequiredService<PlayerSpawnService>();
        var logger      = services.GetService<ILoggerFactory>()?.CreateLogger(nameof(WarpService)) ?? NullLogger.Instance;
        bool changesMap = mapId != player.MapId;

        
        
        
        
        
        
        await RidingService.DropAsync(session, player, services);

        
        
        
        player.ClearRevivalOffers();

        var entityId = player.EntityId;
        var charUid  = player.CharacterUid;

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (changesMap)
            await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, logger, CancellationToken.None);

        
        
        await spawnSvc.NotifyPlayerDespawnAsync(player);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (changesMap)
            await SendPreMapLoadBurstAsync(session, player, data, services);

        
        
        
        
        
        
        
        
        
        
        
        
        if (!changesMap)
            await IllusionPyramidService.CloseOnMapLoadAsync(session, player, services, logger, CancellationToken.None);

        
        
        
        player.MapLoaded = false;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnReceiveMapLoad,
            WorldEntryService.BuildMapLoad2311(entityId, mapId, x, y, z));

        
        
        
        
        {
            using var qs = new PacketWriter();
            qs.WriteUInt32(10);
            qs.WriteUInt64(charUid);
            qs.WriteUInt16(0);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnQuickSlotSaveData, qs.GetWrittenMemory());
        }
        IReadOnlyList<OwnedVehicle> vehicles = Array.Empty<OwnedVehicle>();
        try
        {
            vehicles = await services.GetRequiredService<IVehicleRepository>().GetAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name} warp: could not load owned vehicles", player.Name);
        }
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnVehicleQuickSlot, VehiclePackets.BuildVehicleQuickSlot2523Empty());
        
        
        VehicleClientList.Record(session.SessionId, vehicles);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAllVehicleList,
            VehiclePackets.BuildAllVehicleList2522(vehicles, VehiclePackets.NowOle()));

        
        
        
        
        
        CollectionDocument? collection = null;
        try
        {
            collection = await CollectionService.From(services, logger).GetStateAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name} warp: could not load the collection state", player.Name);
        }
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyMCollection,
            WorldEntryService.BuildNfyMCollection2913(collection?.RegisteredMonsterIds.Count ?? 0, data));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCollection,
            CollectionPackets.BuildNfyPCollection(collection?.RegisteredPetIds.Count ?? 0));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPCollectionSetActivePet, collection is { ActivePetId: not 0 }
            ? CollectionPackets.BuildAnsPCollectionSetActivePet(collection.ActivePetId, collection.ActivePetFlag, 0)
            : CollectionPackets.BuildAnsPCollectionSetActivePet(0, 0, 30001));

        
        
        
        
        
        
        
        
        player.MapId = mapId; player.X = x; player.Y = y; player.Z = z;

        
        
        return true;
    }

 /// <summary>
 /// Sends the cross-map part of a warp for a player already in the world: saved key bindings, worn gear and live HP and SP.
 /// </summary>
 /// <remarks>
 /// The character sheet is built from the world-entry snapshot, synced to the current level, so max HP and SP do not regress after a level-up.
 /// A storage failure while loading key bindings sends the no-config packet instead of skipping the burst.
 /// </remarks>
    private static async Task SendPreMapLoadBurstAsync(ProudSession session, PlayerState player, GameDataStore data,
        IServiceProvider services)
    {
        var character = player.Character
            ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
        if (character is null) return;
        if (player.Level != 0 && character.Level != player.Level) character.Level = player.Level;

        var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(player.CharacterId);
        KeyConfigDocument? keyConfig = null;
        try
        {
            keyConfig = await services.GetRequiredService<RO2.Application.Interfaces.IKeyConfigRepository>()
                .GetAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(nameof(WarpService))
                .LogError(ex, "{Name} warp: could not load key config", player.Name);
        }

        
        
        var trait = services.GetRequiredService<EffectService>().BuildInWorldPcTrait(player, character, equipped);
        await WorldEntryService.SendPreMapLoadBurstAsync(session, character, player.EntityId, keyConfig, trait);
    }
}
