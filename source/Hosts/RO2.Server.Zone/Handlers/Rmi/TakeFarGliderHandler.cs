using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1515 ReqTakeFarGlider - Mount far/flight glider</summary>
public class TakeFarGliderHandler : ZoneRmiHandlerBase<TakeFarGliderHandler>
{
    /// <summary>Offset of the u16 destination station id in the 1515 body (after the compact envelope, msgTag and u32 handle).</summary>
    private const int StationIdOffset = 10;

    /// <summary>Body length needed to read the station id: through its two bytes.</summary>
    private const int StationIdEnd = StationIdOffset + sizeof(ushort);

    /// <summary>Station id used when the body is too short to carry one.</summary>
    private const ushort NoStation = 0;

    public override ushort Opcode => ZoneC2SOpcodes.ReqTakeFarGlider;
    public override string Name => "TakeFarGlider";
    public TakeFarGliderHandler(IServiceScopeFactory s, ILogger<TakeFarGliderHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Destination station: C2S ReqTakeFarGlider (1515) is compact-BA and carries the destination
        // station id as a u16 at body+10 (after compact envelope + msgTag + u32 handle).
        ushort station = payload.Length >= StationIdEnd
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Span.Slice(StationIdOffset))
            : NoStation;

        // Gate: the station must be a far-glider station (GliderRoutes.TryGet drops unknown ids and the
        // npc_id 99990000 ride rows) and start on the map the player is on. A refused 1515 is LOG-AND-IGNORE: no 2731,
        // no flight token, nothing moves.
        if (!GliderRoutes.TryGet(station, out _) || !GliderRoutes.StartsOnMap(station, player.MapId))
        {
            Logger.LogWarning("{Name} far-glider station {St} refused: not a station or wrong map ({Map}) - ignored",
                player.Name, station, player.MapId);
            return;
        }
        // The interaction radius is not enforced (GliderRoutes.NearStartRadius): whether the player stands near
        // the station start only logs.
        if (!GliderRoutes.IsNearStart(station, player.MapId, player.X, player.Y))
            Logger.LogDebug("{Name} far-glider station {St} from beyond the unverified near-start radius (map {Map}); allowed",
                player.Name, station, player.MapId);

        // The 1528 that follows must present this token (single use, same station, within its validity window).
        GliderRideStore.IssueFlightToken(player, station, DateTime.UtcNow + GliderRideStore.FlightTokenLifetime);

        // 1515 STARTS the flight. OnMountFarGlider 2731 is what actually kicks the client's glider off:
        // the client requires result@blob+8 == 0, dismisses any current vehicle, and hands gliderId@blob+10 to
        // the glider UI, which plays the flight cutscene and loads the destination map CLIENT-SIDE. Without this the
        // client never flies and never sends the following 1613/1528, so nothing happens. Body is
        // [u32 riderHandle][u16 result=0][u16 gliderId] (BuildOnMountFarGlider).
        //
        // The RELOCATION is NOT done here — it is committed server-side on the 1528 the client sends
        // after the cutscene (GliderTeleportHandler), with NO 2311, because the client already loaded the
        // map itself. (Also sending a 2311 warp on the 1528 causes a double map load and a disconnect.)
        var body = ActorStatePackets.BuildOnMountFarGlider(player.EntityId, station);
        await session.SendRmiAsync(ZoneS2COpcodes.OnMountFarGlider, writer => writer.WriteBytes(body));
        Logger.LogInformation("{Name} far-glider station {St}: mount + client cutscene (relocation on the 1528)",
            player.Name, station);
    }
}
