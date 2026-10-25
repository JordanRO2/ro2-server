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
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1314 ReqGetOffGlider — dismount the ride glider mounted by ReqTakeGlider (1313), at a position.
/// Ride/short glider, NOT the far-glider Kafra flight.
///
/// C2S layout: COMPACT-BA, blob 20B, msgTag 0xE307 (58119). Blob-relative (blob+X == payload+X+2):
///   [u16 inner=20][u16 tag=0xE307][u32 0][f32 x@8][f32 y@12][f32 z@16]
/// The three floats are the dismount position. The request carries no route id.
///
/// Server action: charge the ride fare, put the server-side position at the ride's landing point (see
/// POSITION), then reply OnUnmountGlider (2442), the dismount confirm. 2442 is COMPACT-BA, msgTag 0xE308 (58120),
/// blob 32 / body 34:
///   [u16 32][u16 0xE308][u32 rider@4][u16 result@8][f32 x@10][f32 y@14][f32 z@18][u64 zeny@22][u16 route@30]
/// A non-zero result makes the client show the result code and stop. Otherwise the client dismisses the vehicle,
/// moves the local player to the position at blob+10/14/18, stores zeny@22 as the wallet and refreshes the wallet
/// widget, then reads route@30 to look up the route's row for the chat line it prints. The client does read the
/// position, so an echo of the request's floats would put the rider where the request said instead of at the
/// ride's landing point.
///
/// POSITION is the ride's landing point, not the request's: x/y/z@10 equal the spline.json
/// end_x/end_y/end_z of route@30. The request's own floats usually land within a couple of units of the end
/// point. The server takes the end point as the rider's position and sends it; without a recorded route (no
/// spline row to read) the request's position is all there is.
///
/// RESULT is always 0: a dismount is never refused, not even for money.
///
/// FARE. zeny@22 is the wallet after the ride fare: the zeny of the 2441 mount confirm (blob+16) minus the
/// spline.json <c>cost</c> of the ride's route. route@30 is that route id, equal to the 2441's blob+14. The route
/// comes from the 1313 (TakeGliderHandler echoes its blob+12 at 2441 blob+14), so TakeGliderHandler has to hand
/// it over through <see cref="GliderRideStore.PutRide"/>. The dismount request cannot supply it: several routes
/// share each landing point, so the landing point alone does not identify the route.
/// A rider who cannot pay is charged what the wallet holds (min(fare, balance)) and still dismounts.
/// Without a recorded route no fare is taken and route 0 is sent, but zeny@22 still carries the real wallet.
///
/// Then OnUnmountGliderNotify (2465) to the rider and to everyone nearby, the counterpart of the 2464
/// TakeGliderHandler sends.
/// </summary>
public class GetOffGliderHandler : ZoneRmiHandlerBase<GetOffGliderHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGetOffGlider;
    public override string Name => "GetOffGlider";

    private const ushort OnUnmountGliderTag = 0xE308; // 58120, constant for every 2442

    /// <summary>Route id 0: no recorded route (no fare is taken, route 0 is sent in the 2442).</summary>
    private const ushort NoRoute = 0;

    /// <summary>2442 result code 0: the dismount succeeded. Always sent; a dismount is never refused.</summary>
    private const ushort DismountOk = 0;

    public GetOffGliderHandler(IServiceScopeFactory s, ILogger<GetOffGliderHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The ride record is taken once (a repeated 1314 cannot charge the fare twice). With no record
        // this is a forged dismount: log and ignore, nothing parsed, no position write, no 2442/2465.
        if (!GliderRideStore.TryTakeRide(player, out var route))
        {
            Logger.LogWarning("{Name} ReqGetOffGlider with no ride record: ignored", player.Name);
            return;
        }

        var spline = services.GetRequiredService<GameDataStore>().Spline.FirstOrDefault(s => s.index == route);
        int fare = spline?.cost ?? 0;

        // The rider stands at the ride's landing point (see POSITION): the server-side position, the
        // 2442 below and the 2465 record (written from player.X/Y/Z) all carry it. The request's floats are
        // never read; a row with no usable end point leaves the rider where the server has them.
        var (x, y, z) = DismountPosition(spline, player.MapId, player.X, player.Y, player.Z);

        if (fare > 0)
        {
            // The fare is charged here at the dismount, and the dismount ALWAYS completes with result 0.
            // The debit is a deliberate clamp to the balance, min(fare, balance), never the
            // refuse-on-insufficient TryDebit: a refusal leaves the rider stuck mounted (the client stops on a
            // non-zero result). The clamp is a defensive choice so the rider is never stranded.
            player.DebitUpTo(fare);
            await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        }
        player.X = x; player.Y = y; player.Z = z;

        // One ride on the daily Kafra-transport board (ranking type 1), see RankingService.
        // Counted where the route is taken, once per mounted ride, so a repeated 1314 does not count it twice.
        if (route != NoRoute)
            await RankingService.CountAsync(services, player, RankingSchedule.TypeKafraTransport, Logger);

        var body = BuildOnUnmountGlider(player.EntityId, x, y, z, player.Gold, route);
        await session.SendRmiAsync(ZoneS2COpcodes.OnUnmountGlider, writer => writer.WriteBytes(body));

        Logger.LogInformation("{Name} dismounted ride-glider route {Route} at ({X:F0},{Y:F0},{Z:F0}), fare {Fare}z -> {Gold}z",
            player.Name, route, x, y, z, fare, player.Gold);

        // 2465 OnUnmountGliderNotify after the 2442, to the rider and to everyone nearby: the rider's PC
        // record without the mask-0x0080 mount id (see GliderNotifyPackets).
        // Its position is the 2442 one, i.e. the spline end. The record
        // is written from player.X/Y/Z, which was set to that same x/y/z above.
        await GliderNotifyPackets.SendUnmountAsync(session, player,
            services.GetRequiredService<IInventoryRepository>(), services.GetRequiredService<IGuildEmblemRepository>(), Broadcast, Logger);
    }

    /// <summary>
    /// Where a ride ends: the spline.json end point of its route (<paramref name="spline"/>), which is what
    /// the 2442 and 2465 carry; the request's own floats when there is no
    /// route row to read, or when the row ends on another map than the rider's. The ride routes all start and
    /// end on their own map; the rows
    /// that change map belong to the Kafra far-glider NPCs (22090000, 22100000, 27190000), which a 1313 never
    /// names, and an end point on another map would put the rider at that map's coordinates on this one.
    /// </summary>
    internal static (float X, float Y, float Z) DismountPosition(RO2.GameData.Tables.Spline? spline,
        uint riderMapId, float requestX, float requestY, float requestZ)
        => spline != null && spline.emap_index == riderMapId
            ? (spline.end_x, spline.end_y, spline.end_z)
            : (requestX, requestY, requestZ);

    // 2442 OnUnmountGlider — COMPACT-BA. Blob (32B): [u16 inner=32][u16 tag=0xE308][u32 rider][u16 result]
    // [f32 x][f32 y][f32 z][u64 zeny][u16 route]. selfLen 32 -> body 34.
    private static byte[] BuildOnUnmountGlider(uint riderHandle, float x, float y, float z, long zeny, ushort route)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(OnUnmountGliderTag);         // blob+2  msgTag 0xE308
            p.WriteUInt32(riderHandle);                // blob+4  rider PC handle
            p.WriteUInt16(DismountOk);                 // blob+8  result 0
            p.WriteSingle(x);                          // blob+10 dismount X
            p.WriteSingle(y);                          // blob+14 dismount Y
            p.WriteSingle(z);                          // blob+18 dismount Z
            p.WriteUInt64((ulong)Math.Max(0, zeny));   // blob+22 wallet after the fare
            p.WriteUInt16(route);                      // blob+30 ride route id (2441 blob+14)
        });
        return pw.ToArray();
    }
}
