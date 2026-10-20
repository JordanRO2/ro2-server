using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1653 ReqGuildEmergencyCallAnswer — a guild member accepts the guild master's Emergency Call
/// (guild skill "긴급 호출", Guild_Skill_Base 210501..210503: summon every member to the master), and is
/// moved to the master.
/// </summary>
/// <remarks>
/// <para>
/// BODY, 24 bytes raw (no envelope; written by the client when the
/// window's Accept button is pressed):
/// <c>[u32 selfHandle][u32 mapId][f32 x][f32 y][f32 z][u32 1]</c>. All of it describes the ANSWERING
/// member, not the destination: the client fills the handle and the position from the local player, the
/// map from the current map, and the last word with the constant 1. The handle carries the
/// PC tag (17) and mapId is the member's own map (1100, 1300, 3101, 3201). The window's
/// other button only closes it, so a refusal sends nothing, and so does the timeout.
/// </para>
/// <para>
/// THE CLIENT-EXPECTED SEQUENCE, by whether the master's map is the member's, each ending in a 2311:
/// <list type="bullet">
/// <item>another map: FIRST the member's own rich 2315 OnPCViewData, ahead of any removal. Its record is the
/// member as the server knows it: the position is the current position of the member's last 1526 (that 1526's +40,
/// not the position in the 1653), and its effect list still holds the effects the warp is about to end (it is
/// longer than the 2315 of the removal that follows). Then the Warp_Handling 0 removals, each 2498 2493 2494, with a
/// SECOND, shorter self 2315 and a 2497 for the visible/speed ones. Then 2598 <c>[00000000]</c>, 2938 <c>[01 00]</c>,
/// the self 2505, the kept effects' 2498 runs, the pre-map-load burst and the 2311 to the master's map;</item>
/// <item>the same map: the self 2505 first, then the destination's 2504/2506/2512 and
/// the old area's 2505/2507/2513, then the 2311. No 2315, no 2938 and no 2598.</item>
/// </list>
/// The 2311 lands on the master's cast position in every answer (see GuildEmergencyCallService).
/// A 1653 with no call to answer gets nothing and no warp.
/// </para>
/// <para>
/// WHAT THIS SERVER SENDS. The call recorded when the master's 1509 landed (GuildEmergencyCallService) is
/// the only source of the destination: warping to the master's live position without one would let any
/// member summon themselves to the master at will. On a valid answer:
/// <list type="bullet">
/// <item>another map (<see cref="DepartCrossMapAsync"/>): the opening self 2315, built by
/// ActorStatePackets.BuildOnPCViewData2315 (the record the world entry and the re-dress handlers send) from
/// the PlayerState, whose position ViewGroupMoveHandler keeps at the last 1526's +40, and whose
/// effect list is still whole; then <see cref="WarpDeparture.DepartAsync"/>, exactly as WarpHandler does for
/// 1371 (the Warp_Handling 0 removals, the ride drop, the pyramid close, 2938, the self 2505, the kept
/// effects' runs); then <see cref="WarpService.WarpAsync"/> to the call's map and position (the pre-map-load
/// burst and the 2311). Three deliberate differences. (1) The opening record's rec+12 byte is 36 in
/// the opening record and 0 in the removal 2315s after it; it is the byte PlayerSpawnService.WriteBaseBlock
/// documents as CharInfo+48, state this server does not model, so the shared
/// builder's 0 is sent. (2) The removal's own
/// 2315/2497 re-dress is not sent: EffectService's removal sends neither for any removal (WarpDeparture
/// remarks). (3) The 1106 x8 + 2598 <c>[0]</c> P2P teardown is not sent: this server has no P2P group
/// system, so no handler sends 1106 or 2598.</item>
/// <item>the same map: the self 2505, then <see cref="WarpService.WarpAsync"/>, with no removals (a 40110
/// removal ahead of a warp is Warp_Handling 1, a natural expiry). The AOI exchange
/// (2504/2506/2512, 2505/2507/2513) before the 2311 is not sent, deliberately: the 2311's consumer
/// destroys every game object, which wipes whatever that exchange spawned, and 2504/2315/2591 are re-sent
/// after the 2311. This server repopulates after the 2311 from
/// the client's 1304, which leaves the client in the same state.</item>
/// </list>
/// </para>
/// </remarks>
public class GuildEmergencyCallAnswerHandler : ZoneRmiHandlerBase<GuildEmergencyCallAnswerHandler>
{
    /// <summary>Size in bytes of the 1653 body.</summary>
    private const int BodyLength = 24;

    /// <summary>The constant the client writes in the last body word (+20) of an accepted answer.</summary>
    private const uint AcceptMarker = 1;

    // 1653 body field offsets: [u32 selfHandle][u32 mapId][f32 x][f32 y][f32 z][u32 marker].
    private const int SelfHandleOff = 0;
    private const int MapIdOff = 4;
    private const int XOff = 8;
    private const int YOff = 12;
    private const int ZOff = 16;
    private const int MarkerOff = 20;

    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildEmergencyCallAnswer;
    public override string Name => "GuildEmergencyCallAnswer";
    public GuildEmergencyCallAnswerHandler(IServiceScopeFactory s, ILogger<GuildEmergencyCallAnswerHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return;
        var (selfHandle, mapId, x, y, z, marker) = ParseAnswer(payload.Span);
        // The body describes the sender. A handle that is not the sender's own, or a last word the client
        // never writes, is not an answer this client produced.
        if (selfHandle != player.EntityId || marker != AcceptMarker)
        {
            Logger.LogWarning("{Name} sent a guild emergency call answer for handle {Handle:X8} (own {Own:X8}), marker {Marker}; ignored",
                player.Name, selfHandle, player.EntityId, marker);
            return;
        }

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild is null
            || !GuildEmergencyCallService.TryConsume(guild.Id, player.CharacterId, DateTime.UtcNow, out var call))
        {
            Logger.LogInformation(
                "{Name} answered a guild emergency call from map {MapId} ({X:F1},{Y:F1},{Z:F1}), but no call of its guild is waiting for that answer; nothing sent",
                player.Name, mapId, x, y, z);
            return;
        }
        // Check the map before anything goes out: the 2938 and the self 2505 without a move would take the
        // player off its own client for nothing, and WarpAsync refuses an unknown map after they were sent.
        var data = services.GetRequiredService<GameDataStore>();
        if (data.GetMap(call.MapId) is null)
        {
            Logger.LogWarning("{Name}'s guild emergency call points at map {Map}, which is not in Map_List; not moved",
                player.Name, call.MapId);
            return;
        }

        if (call.MapId != player.MapId)
        {
            // The opening self 2315, then the departure WarpHandler sends for 1371 (class remarks). WarpAsync
            // below then finds the ride dropped and nothing to close.
            await DepartCrossMapAsync(session, player, services, Logger, ct);
        }
        else
        {
            // The same-map answer is an in-place 2311 with no removals and no 2938: only the self 2505 goes
            // ahead of it. Its 29016 pyramid close is WarpAsync's.
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
                PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }));
        }

        var ok = await WarpService.WarpAsync(session, player, call.MapId, call.X, call.Y, call.Z, services);
        Logger.LogInformation("{Name} answered the guild emergency call: from map {From} to map {Map} ({X:F1}, {Y:F1}, {Z:F1}), warped={Ok}",
            player.Name, mapId, call.MapId, call.X, call.Y, call.Z, ok);
    }

    /// <summary>
    /// What a cross-map answer sends before the move (class remarks):
    /// the member's own 2315 OnPCViewData, built from the PlayerState BEFORE anything is removed (the opening
    /// record still lists the effects the warp ends), then <see cref="WarpDeparture.DepartAsync"/>.
    /// </summary>
    /// <remarks>
    /// The record needs the character row: the world-entry snapshot (<see cref="PlayerState.Character"/>, brought
    /// to the live level as the map load does) or the repository. Without one the 2315 is skipped and the departure
    /// still goes out: a self record cannot be built without the character, and the map load restates the PC.
    /// </remarks>
    internal static async Task DepartCrossMapAsync(ProudSession session, PlayerState player, IServiceProvider services,
        ILogger logger, CancellationToken ct)
    {
        var character = player.Character
            ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
        if (character is not null)
        {
            AnsMapLoadingHandler.SyncLiveLevel(player, character);
            var worn = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(player.CharacterId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData,
                ActorStatePackets.BuildOnPCViewData2315(player, character, worn, services.GetRequiredService<IGuildEmblemRepository>()), ct);
        }
        else
        {
            logger.LogWarning("{Name} answered a guild emergency call: no character {Id}, the opening 2315 is not sent",
                player.Name, player.CharacterId);
        }

        await WarpDeparture.DepartAsync(session, player, services, logger, ct);
    }

    /// <summary>Reads the 24-byte 1653 body; see the class remarks for each field.</summary>
    internal static (uint SelfHandle, uint MapId, float X, float Y, float Z, uint Marker) ParseAnswer(ReadOnlySpan<byte> body)
        => (BinaryPrimitives.ReadUInt32LittleEndian(body[SelfHandleOff..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[MapIdOff..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[XOff..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[YOff..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[ZOff..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[MarkerOff..]));
}
