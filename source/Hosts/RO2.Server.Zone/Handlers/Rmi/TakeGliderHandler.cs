using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1313 ReqTakeGlider — mount the summonable "ride" glider (the personal glider a player flies
/// around a map on). This is the SHORT/ride glider (OnMountGlider 2441 / OnUnmountGlider 2442), NOT the
/// far-glider Kafra flight (1515 ReqTakeFarGlider / 1528 ReqGliderTeleport, handled elsewhere).
///
/// C2S layout: COMPACT-BA, blob 18B, msgTag 0xE304 (58116). Blob-relative fields — the blob starts after
/// the 2-byte compact prefix, so blob+X == payload+X+2:
///   [u16 inner=18][u16 tag=0xE304][u32 0][u32 gliderKind@8][u16 route@12][u32 arg0@14]
/// blob+8 is the glider kind: 99990000 (0x05F5B9F0), the npc_id every ride route of spline.json carries.
/// blob+12 is the route: the spline.json index, whose cost is the fare.
///
/// Server action: reply OnMountGlider (2441) — the confirm the client waits on before it starts gliding.
/// 2441 is COMPACT-BA, msgTag 0xE305 (58117), single length 26B, blob
/// <c>[u16 24][u16 tag][u32 rider@4][i16 result@8][u32 kind@10][u16 route@14][u64 zeny@16]</c>.
/// Kind and route echo the request. blob+16 is the WALLET: the client compares it with its displayed zeny
/// and replaces it. It is the balance BEFORE the fare: the fare is taken at the dismount, where the next 2442
/// carries 2441's zeny minus spline[route].cost. (A synthetic vehicle handle there would be shown as the wallet.)
///
/// Then OnMountGliderNotify (2464) to the rider and to everyone nearby: the rider's PC record carrying
/// the mount id under mask bit 0x0080 (see <see cref="GliderNotifyPackets"/>). That mount id is blob+14
/// of this request, so it is taken from the request, never made up.
///
/// A riding pet is dismissed first (RidingService.DropAsync, before the 2441): the rider's drop
/// (2497 2498 2493 2494 2712) goes out between the request and the 2441.
///
/// REFUSAL: a 2441 with result 9 and no 2464, blob
/// <c>[u16 24][tag 0xE305][u32 rider][i16 9][u32 0][u16 0][u64 0]</c>. The client returns early on a non-zero
/// result and shows a message box, so the zeroed kind/route/zeny are never read. What decides the refusal is
/// the requester's PC combat flag, blob+14 of its latest self 2398 OnChangeState. This server has no such
/// state: PlayerState.IsInCombat is only the 1498 auto-attack engagement (skills, DoT and PvP never set it, and
/// nothing clears it when the player walks away from a live monster), so gating on it would lock players out
/// of the glider. Until a PC combat-state tracker exists (set from every damage path, cleared at engagement
/// end, sent as the PC 2398 flag) every 1313 is accepted, which is the answer for a PC out of combat. Do NOT
/// gate on the siege or the route.
/// </summary>
public class TakeGliderHandler : ZoneRmiHandlerBase<TakeGliderHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTakeGlider;
    public override string Name => "TakeGlider";

    /// <summary>msgTag of the 2441 OnMountGlider reply: 0xE305 = 58117 (the request 1313 carries 0xE304 = 58116).</summary>
    private const ushort OnMountGliderTag = 0xE305;

    /// <summary>2441 result: 0 = mounted. A non-zero result (9 = refused) makes the client stop reading the rest of the blob.</summary>
    private const short MountResultOk = 0;

    // 1313 request layout. The handler receives the payload with the 2-byte compact-length prefix still
    // in front, so a blob-relative offset X is payload offset X + CompactPrefixBytes.
    private const int CompactPrefixBytes = 2;
    private const int RequestKindOffset = CompactPrefixBytes + 8;     // u32 glider kind (the ride npc_id)
    private const int RequestRouteOffset = CompactPrefixBytes + 12;   // u16 route (spline.json index)
    private const int RequestMountIdOffset = CompactPrefixBytes + 14; // u32 mount id forwarded to 2464

    public TakeGliderHandler(IServiceScopeFactory s, ILogger<TakeGliderHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        uint gliderKind = span.Length >= RequestKindOffset + sizeof(uint)
            ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(RequestKindOffset)) : 0u;
        ushort route = span.Length >= RequestRouteOffset + sizeof(ushort)
            ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(RequestRouteOffset)) : (ushort)0;
        // blob+14: the mount id 2464 forwards. Every 1313 is 20 bytes (2 prefix + 18 blob), so a shorter one has
        // no id to forward and gets no 2464 rather than a SummonMount(0).
        uint? mountId = span.Length >= RequestMountIdOffset + sizeof(uint)
            ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(RequestMountIdOffset)) : null;
        int payloadLength = span.Length;

        // HSV MV-6: a forged or replayed 1313 is LOG-AND-IGNORE (nothing moved, nothing sent, no 2441/2464).
        // The kind must be the ride npc_id, the route a ride row of spline.json that starts on the rider's map,
        // and not already holding a ride (a second 1313 would overwrite the record and dodge the fare).
        var spline = gliderKind == GliderRoutes.RideNpcId && GliderRoutes.IsRide(route)
            ? services.GetRequiredService<GameDataStore>().Spline.FirstOrDefault(s => s.index == route)
            : null;
        if (spline is null || spline.npc_id != GliderRoutes.RideNpcId || spline.smap_index != player.MapId
            || GliderRideStore.HasRide(player))
        {
            Logger.LogWarning("{Name} ReqTakeGlider refused (kind {K}, route {R}, map {Map}, ride held {Held}): ignored",
                player.Name, gliderKind, route, player.MapId, GliderRideStore.HasRide(player));
            return;
        }
        // The interaction radius is not enforced (GliderRoutes.NearStartRadius). The rider's distance from the
        // route start only logs.
        if (!GliderRoutes.IsNearStart(route, player.MapId, player.X, player.Y))
            Logger.LogDebug("{Name} ReqTakeGlider route {R} from beyond the unverified near-start radius; allowed", player.Name, route);
        // Echoed from the validated spline row, not from the client.
        gliderKind = spline.npc_id;
        route = spline.index;

        // Off the riding pet before the glider (nothing is sent when the player is not riding one).
        await RidingService.DropAsync(session, player, services);

        // The live wallet (PlayerState.Gold is the runtime balance every zeny change writes), unchanged:
        // the ride is charged at the dismount, not here (see the summary).
        var body = BuildOnMountGlider(player.EntityId, gliderKind, route, player.Gold);
        await session.SendRmiAsync(ZoneS2COpcodes.OnMountGlider, writer => writer.WriteBytes(body));
        // The dismount (1314) carries no route, and it is where this route's fare is taken and the route is echoed
        // at 2442 blob+30 (equal to this 2441's blob+14), so hand it over now.
        GliderRideStore.PutRide(player, route);

        Logger.LogInformation("{Name} mounted ride-glider (kind {K}, route {R}), wallet {Gold}",
            player.Name, gliderKind, route, player.Gold);

        // 2464 right after the 2441: the rider's own 2464 follows its 2441 immediately.
        if (mountId is not uint id)
        {
            Logger.LogWarning("{Name} ReqTakeGlider is {Len} bytes, too short for the mount id at blob+14; 2464 not sent.",
                player.Name, payloadLength);
            return;
        }
        await GliderNotifyPackets.SendMountAsync(session, player, id,
            services.GetRequiredService<IInventoryRepository>(), services.GetRequiredService<IGuildEmblemRepository>(), Broadcast, Logger);
    }

    // 2441 OnMountGlider — COMPACT-BA. Blob (24B): [u16 inner=24][u16 tag=0xE305][u32 rider][i16 result]
    // [u32 kind][u16 route][u64 zeny]. selfLen 24 -> body 26 (matches the wire).
    private static byte[] BuildOnMountGlider(uint riderHandle, uint gliderKind, ushort route, long zeny)
    {
        using var pw = new PacketWriter();
        // WriteCompactBABody writes the envelope: the compact-length marker and length byte (01 18), then the
        // u16 innerSize (24) at blob+0. The lambda writes the rest of the 24-byte blob.
        pw.WriteCompactBABody(blob =>
        {
            blob.WriteUInt16(OnMountGliderTag);             // blob+2  msgTag 0xE305
            blob.WriteUInt32(riderHandle);                  // blob+4  rider PC handle (low byte 0x11)
            blob.WriteInt16(MountResultOk);                 // blob+8  result: 0 = mounted
            blob.WriteUInt32(gliderKind);                   // blob+10 echoed from the request's blob+8
            blob.WriteUInt16(route);                        // blob+14 echoed from the request's blob+12
            blob.WriteUInt64((ulong)Math.Max(0, zeny));     // blob+16 wallet shown by the client
        });
        return pw.ToArray();
    }
}
