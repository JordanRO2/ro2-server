using RO2.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1528 ReqGliderTeleport — the actual glider teleport (the far-glider 1515 only starts the cutscene;
/// this is what the client sends after it, and it also stands alone for a short glider hop).
///
/// A cross-map glider is a FULL RE-ENTRY to the destination map: the whole world-entry
/// burst is replayed (2558/2743/2595/2498/2669 -> 2311 -> 2472/2523/2522 -> collection cluster), then after the
/// client's AnsMapLoading the destination NPCs are spawned (2506) and finally OnUnmountFarGlider 2732 takes the
/// player off the glider. The load-bearing part is the PRE-2311 burst: it refreshes the
/// zone-enter character record that the 2311 handler writes into. Sending 2311 without it leaves that record
/// stale and the client dereferences a null pointer about a second after arrival.
///
/// So this re-runs WorldEntryService.SendWorldEntryAsync in WARP mode (which skips the login-only
/// 2310/2535/2488/2982 and relocates to the passed destination), and flags AnsMapLoading to emit the
/// 2732 dismount once the arrival map is populated.
/// </summary>
public class GliderTeleportHandler : ZoneRmiHandlerBase<GliderTeleportHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGliderTeleport;
    public override string Name => "GliderTeleport";

    /// <summary>Body offset of the destination station id (u16), the same layout as the far-glider 1515.</summary>
    private const int StationIdOffset = 10;

    /// <summary>Minimum body length that holds the station id: <see cref="StationIdOffset"/> + the 2-byte u16.</summary>
    private const int MinBodyLength = StationIdOffset + sizeof(ushort);

    /// <summary>
    /// The client sends 1528 twice per flight, about 16 ms apart. Any 1528 inside this window after an accepted one
    /// is a duplicate and is dropped.
    /// </summary>
    private const double DuplicateWindowSeconds = 3.0;

    public GliderTeleportHandler(IServiceScopeFactory s, ILogger<GliderTeleportHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Destination station id (u16 at body+StationIdOffset, same layout as far-glider 1515).
        ushort station = payload.Length >= MinBodyLength
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Span.Slice(StationIdOffset))
            : (ushort)0;

        if (!GliderRoutes.TryGet(station, out var dest))
        {
            Logger.LogWarning("{Name} glider unknown station {St}", player.Name, station);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();

        // De-dupe: the client sends 1528 TWICE per flight (~16 ms apart). Process only the first, or the
        // second re-runs the whole re-entry and drops the socket. Read here, stamped only once the gate below passes,
        // so a refused 1528 neither opens nor extends the window.
        var now = DateTime.UtcNow;
        if ((now - player.LastGliderWarpUtc).TotalSeconds < DuplicateWindowSeconds)
        {
            Logger.LogDebug("{Name} duplicate glider 1528 (station {St}) within 3s — ignored", player.Name, station);
            return;
        }

        // Gate. The station's row must start on the map the player is on. A cross-map hop must also present
        // the single-use flight token the 1515 TakeFarGlider issued for this station. A SAME-MAP hop
        // (smap_index == emap_index) is exempt from the token, since demanding one could refuse a legitimate
        // short hop. A refused 1528 is logged and ignored: nothing sent, nothing moved, no fare taken.
        var spline = System.Linq.Enumerable.FirstOrDefault(store.Spline, s => s.index == station);
        if (spline is null || spline.smap_index != player.MapId)
        {
            Logger.LogWarning("{Name} glider station {St} refused: row starts on map {Start}, player is on map {Map} - ignored",
                player.Name, station, spline?.smap_index, player.MapId);
            return;
        }
        if (spline.smap_index != spline.emap_index && !GliderRideStore.TryConsumeFlightToken(player, station, now))
        {
            Logger.LogWarning("{Name} glider station {St} refused: no valid 1515 flight token - ignored", player.Name, station);
            return;
        }
        player.LastGliderWarpUtc = now;

        if (store.GetMap(dest.MapId) is null)
        {
            Logger.LogWarning("{Name} glider dest map {Map} not in data — aborting", player.Name, dest.MapId);
            return;
        }

        // Load the character up front — needed (cross-map) for the 2311/2315 re-dress.
        var characterRepo = services.GetRequiredService<ICharacterRepository>();
        var character = await characterRepo.GetByIdAsync(player.CharacterId);
        if (character is null)
        {
            Logger.LogWarning("{Name} glider: character {Id} not found", player.Name, player.CharacterId);
            return;
        }

        // Charge the glider fare: the destination station id IS the spline.json 'index', and its 'cost' is
        // the fare. The new balance reaches the client in the arrival's 2732 OnUnmountFarGlider (u64 at blob+8,
        // the previous balance minus spline[station].cost); no 2435 is sent for it. So this handler only updates
        // the wallet and the cross-map arrival path delivers it: the 2732 (BuildUnmountFarGlider2732's zeny,
        // player.Gold) alone carries the new balance (UpdateGoldAsync below persists it).
        // PlayerState.Gold is the runtime wallet (write-behind persisted); the row loaded above can lag it,
        // so the fare comes off the live value. Runs after the once-per-flight de-dupe guard above.
        int fare = spline.cost;
        if (fare > 0)
        {
            // Taken atomically, never clamped: a rider who cannot pay is refused (no flight, nothing sent),
            // so the flight is not free.
            if (!player.TryDebit(fare))
            {
                Logger.LogInformation("{Name} glider station {St} refused: fare {Fare}z, wallet {Gold}z",
                    player.Name, station, fare, player.Gold);
                return;
            }
            long newGold = player.Gold;
            character.Gold = newGold;
            await characterRepo.UpdateGoldAsync(player.CharacterId, newGold);
            Logger.LogInformation("{Name} glider fare {Fare}z charged (station {St}) -> {Gold}z left",
                player.Name, fare, station, newGold);
        }

        // One flight on the daily Kafra-transport board (ranking type 1), which counts 1515 ReqTakeFarGlider flights
        // (RankingService). Counted here, past the once-per-flight de-dupe and where the fare is taken, because the
        // 1528 is one per flight too: every 1528 that is not the duplicate follows a 1515.
        await RankingService.CountAsync(services, player, RankingSchedule.TypeKafraTransport, Logger);

        // SAME-MAP hop: the station resolves to the map the player is already on. Nothing is replied
        // here — the client just moves itself to the destination point on the current map. A full
        // world re-entry would re-load the map the client already has and crash it during that reload
        // (the client never even reaches AnsMapLoading). So only update the server-side position.
        if (dest.MapId == player.MapId)
        {
            player.X = dest.X; player.Y = dest.Y; player.Z = dest.Z;
            // No arrival, so no 2732 carries the new balance on this path: the 2435 is the only way the
            // wallet shows the fare.
            if (fare > 0)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
                    ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold));
            Logger.LogInformation("{Name} glider station {St} same-map hop -> ({X:F0},{Y:F0},{Z:F0})",
                player.Name, station, dest.X, dest.Y, dest.Z);
            return;
        }

        // Quick-slot bar (2472). Skills are only used by the login-only 2488 a warp skips. The bag (loaded below,
        // after the departure steps) feeds only the burst's MVP-card 2588s in warp mode: the 2498 it would build is
        // replaced by the in-world warpSheet, and the 2310 that lists the bag is login-only.
        var quickSlots = await services.GetRequiredService<IQuickSlotRepository>().GetByCharacterAsync(player.CharacterId);
        // Collection state for the 2913/2919/2918 the warp burst re-sends on a
        // glider re-entry too. Not passing it made them always say "nothing registered, no active pet".
        var collection = await CollectionService.From(services, Logger).GetStateAsync(player.CharacterId, ct);
        // Saved key bindings for the warp burst's 2558: the list is re-sent on a glider re-entry,
        // so the empty form here would drop them.
        KeyConfigDocument? keyConfig = null;
        try
        {
            keyConfig = await services.GetRequiredService<RO2.Application.Interfaces.IKeyConfigRepository>()
                .GetAsync(player.CharacterId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name} glider: could not load key config", player.Name);
        }
        // Owned vehicles for the warp burst's 2522: the list is re-sent on every map load, a glider arrival
        // included. A storage error sends the empty list.
        IReadOnlyList<OwnedVehicle>? vehicles = null;
        try
        {
            vehicles = await services.GetRequiredService<IVehicleRepository>()
                .GetAsync(player.CharacterId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name} glider: could not load owned vehicles", player.Name);
        }

        // The effects a warp ends (Effect_Table.Warp_Handling 0), then the ride, ahead of the pyramid close and the 2938,
        // on the old map: the 2494 of each removal goes to player.MapId, so this runs before the despawn and the MapId
        // commit below, and before the arrival 2315 is built, because a removal can lower the max HP that record
        // carries (60043 has a +500 max-HP row). They are removed ahead of
        // the 2909 when there is one (see WarpDeparture).
        var effects = services.GetRequiredService<EffectService>();
        await WarpDeparture.DropWarpEffectsAsync(player, store, effects);
        await RidingService.DropAsync(session, player, services);

        // Build the PC-appearance record (2315) and arm the dismount FLAGS FIRST — BEFORE the map-load
        // burst below. The 2311 is sent mid-burst, and a fast client acks it with AnsMapLoading (1304)
        // before the burst finishes; arming the flags afterward raced, so AnsMapLoadingHandler saw them
        // unset and skipped the re-dress (the arrived PC then showed the default look, no name). The 2311
        // destroyed and re-created the PC, so AnsMapLoadingHandler re-dresses it (name/gender/class/speed)
        // once the destination map has loaded, then dismounts the glider and re-unblocks movement.
        // Worn gear feeds the 2315 equipment vectors (VEC28 dresses this PC on other screens, VEC23 is the
        // self equip list the C panel reads). Loaded here where the character is already in hand.
        var inventory = services.GetRequiredService<RO2.Application.Interfaces.IInventoryRepository>();
        var equipped = await inventory.GetEquipmentAsync(player.CharacterId);
        var appear = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson);
        player.PendingGliderViewData = ActorStatePackets.BuildOnPCViewData2315(
            player.EntityId, player.CharacterUid, character.Name, appear.Race, (byte)character.Gender,
            dest.X, dest.Y, dest.Z, player.Rotation,
            (uint)System.Math.Max(0, player.Hp), (uint)System.Math.Max(0, player.MaxHp),
            (ushort)System.Math.Clamp(player.Mp, 0, ushort.MaxValue),
            (ushort)System.Math.Clamp(player.MaxMp, 0, ushort.MaxValue),
            character.Level, character.Job, character.JobLevel, equipped,
            // Same slots as the login self-2315 in AnsMapLoadingHandler: the login 2310's
            // appearance block, model class and body[140..141] u16 are repeated in every self 2315, glider arrivals
            // included (see the BuildOnPCViewData2315 parameter docs). Left at the 0 defaults, each arrival
            // differed from the 0x10 the login 2315 cached, which re-runs the client's model rebuild.
            // rec+67 (the builder's masterLevel parameter) is the EQUIP HIDE FLAG: that 2310 u16 equals the char
            // list's [75..76] and follows 1312 (see PlayerSpawnService.WriteRecord).
            // Character.MasterLevel is never assigned, so passing it zeroed CharInfo+530 on every
            // arrival, undoing the flag the 2310 set.
            appearanceRaw: appear.RawBytes,
            modelClass: ActorStatePackets.DetailTailModelClass,
            masterLevel: character.EquipHideFlag);
        player.PendingGliderDismount = true;
        // The client has no map from here until its 1304, so MapLoaded must say so too. Clearing it here
        // (not only relying on PendingGliderDismount) keeps the flag truthful for any gate that reads it
        // alone, and the 1304 reopens it at AnsMapLoadingHandler before any of its awaited sends — so an
        // exception later in this handler cannot leave the party-search gate closed for the session.
        player.MapLoaded = false;

        var spawnSvc = services.GetRequiredService<PlayerSpawnService>();

        // Settle and close the Illusion Pyramid ahead of the 2938, on the old map: an unsolicited 2909
        // [0][1] (after the 2329 / 2333 of a held item) is sent between the 2901 and the 2938 when the pyramid was used since the
        // last 2938. This path keeps the PlayerState, so without it the window stayed open over the arrival and the
        // run stayed held. Not sent on the same-map hop above, which sends no 2938. See
        // IllusionPyramidService, LEAVING.
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, Logger, ct);

        // Despawn from the OLD map (broadcast, excl. self) BEFORE moving MapId, then commit the destination
        // so the AnsMapLoading NPC spawn targets it.
        await spawnSvc.NotifyPlayerDespawnAsync(player);
        player.MapId = dest.MapId; player.X = dest.X; player.Y = dest.Y; player.Z = dest.Z;

        // Ack the teleport (2938, flat u16=1), then replay the world-entry burst in warp mode.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, writer => writer.WriteBytes(SessionExitPackets.Ack2938));

        // OnPC_Out 2505 to the DEPARTING client itself: the self actor is despawned from the old map at
        // this exact point (right after the AnsGliderTeleport, before the map-load burst), so the client
        // tears down its own actor cleanly before the 2311 destroy.
        await session.SendRmiAsync(ZoneS2COpcodes.OnPC_Out, writer =>
            writer.WriteBytes(PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId })));
        // The warp burst's 2498 is the in-world sheet (gear, live HP/SP, active effects), at the live level, like
        // WarpService's; without it the burst sent the donor sheet with neither gear nor effects.
        AnsMapLoadingHandler.SyncLiveLevel(player, character);

        // The kept effects' 2499 lines, each effect closed by one 2498, between the self 2505 and the 2558 of the burst
        // (e.g. 40000, 2498, 40009, 2498, ... then 2558). See WarpDeparture.SendKeptEffectLinesAsync.
        await WarpDeparture.SendKeptEffectLinesAsync(session, player, store, effects, character, equipped);

        // The bag for the burst's 2588 AnsRegistCardBook, one per MVP card (ItemInfo Item_Type 139) held, sent by
        // SendWorldEntryAsync right after the 2913/2919/2918 trio (CardRegistrationNotifier.HeldMvpCards filters the
        // bag rows itself, so it is passed whole). They are sent in a cross-map re-entry burst too, with no 2310 in
        // it. Loaded here, after the
        // pyramid close above, so an item that close moved is current. Nothing else in warp mode reads it (see the
        // quick-slot note above). A storage error sends no 2588, as before.
        IReadOnlyList<RO2.Domain.Entities.InventoryItem>? bag = null;
        try
        {
            bag = await inventory.GetByCharacterAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name} glider: could not load the bag for the MVP-card 2588s", player.Name);
        }

        var warpSheet = effects.BuildInWorldPcTrait(player, character, equipped);
        await WorldEntryService.SendWorldEntryAsync(
            session, character, player.CharacterUid, player.EntityId, Logger,
            inventoryItems: bag, activeQuests: null, completedQuestCount: 0, learnedSkills: null,
            gameData: store, quickSlots: quickSlots,
            isWarp: true, warpSpawn: (dest.MapId, dest.X, dest.Y, dest.Z),
            collection: collection, keyConfig: keyConfig, warpPcTrait2498: warpSheet, vehicles: vehicles);

        // Let players already on the destination see the arrival.
        await spawnSvc.NotifyPlayerSpawnAsync(player);

        Logger.LogInformation("{Name} glider station {St} -> map {Map} ({X:F0},{Y:F0},{Z:F0}) (full re-entry)",
            player.Name, station, dest.MapId, dest.X, dest.Y, dest.Z);
    }
}
