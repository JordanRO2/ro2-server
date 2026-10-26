using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1677 ReqTeleportSavePoint (Empty) - Teleport to save point (/escape)</summary>
/// <remarks>
/// <para>
/// Empty body. The client sends it for the /escape command.
/// </para>
/// <para>
/// The 3010 AnsTeleportSavePoint is the refusal: an 18-byte 3010 and no warp. It carries the time left before
/// the next escape (<see cref="TeleportSavePointPackets"/>), and is sent when the 1677 falls inside the 60 minutes
/// after that character's previous escape (<see cref="EscapeCooldown"/>). So a 1677 inside the cooldown gets it and
/// stays put, and only a warp that happens starts the cooldown.
/// </para>
/// <para>
/// To another map, the answer is the departure form of a map change: 2938 <c>01 00</c>, the self 2505, the
/// pre-map-load burst, the 2311 to the save point, the real quick-slot bar 2472, 2523 2522 and 2913 2919 2918,
/// with the destination's NPCs sent only in the reply to the client's 1304. That is <see cref="WarpEntryBurst"/>
/// (see there for the parts not reproduced), the glider re-entry's form. WarpService.WarpAsync sends no 2938 and no
/// self 2505, sends the destination's 2506 before the 2311 (so the NPCs would come twice: AnsMapLoadingHandler
/// sends them again after the 1304), an empty 2472 and no 2913/2919/2918, so it is not used here.
/// </para>
/// <para>
/// On the save point's own map the in-place form is used: self 2505 -> 2506 -> 2512 -> 2311 -> 2472 -> 2523 -> 2522,
/// with no 2938, no pre-map-load burst and no 2913/2919/2918, and a 2472 carrying the character's real bar. That is
/// <see cref="WarpEntryBurst.SendInPlaceAsync"/>. WarpService.WarpAsync writes a count-0 2472, which empties the
/// client's bar until the next map change or relog, and also sends 2913/2919/2918, so it is not used here.
/// </para>
/// <para>
/// The destinations are the ones <see cref="ResolveSavePoint"/> computes: 1200
/// (728, 537, 57) = Position_Info 103 "Alberta save point" when the bound index is 103,
/// 1100 (739, 767, 56) = Position 13, 1300 (447, 333, 12) = Position 314 "Morroc revival point",
/// and 1100 (610, 969, 43) from map 3101, which is Map_List 3101's own return point, not a bind.
/// </para>
/// </remarks>
public class TeleportSavePointHandler : ZoneRmiHandlerBase<TeleportSavePointHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTeleportSavePoint;
    public override string Name => "TeleportSavePoint";

    public TeleportSavePointHandler(IServiceScopeFactory s, ILogger<TeleportSavePointHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // HSV MV-10: gate first so a refusal does not consume the cooldown. Dead, in combat or trading: ignored.
        // No refusal is sent for these states; the request is ignored.
        if (TeleportSavePointUseCase.IsIgnored(player.IsDead, player.IsInCombat, TradeSessionService.Instance.Get(player.SessionId) is not null))
        {
            Logger.LogDebug("{Name} /escape ignored (dead={Dead}, combat={Combat}, or trading)", player.Name, player.IsDead, player.IsInCombat);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();

        // Inside the cooldown: the 3010 with the whole seconds left, and no warp. The claim is taken before the
        // warp so two 1677s racing cannot both go through, and given back if the warp does not happen.
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!EscapeCooldown.TryClaim(player.CharacterId, nowUnix, EscapeCooldown.Length(data.ConfigDef),
                out long remaining, out long? previousEscape))
        {
            Logger.LogDebug("{Name} /escape refused: {Seconds} s of cooldown left", player.Name, remaining);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsTeleportSavePoint,
                TeleportSavePointPackets.BuildAnsTeleportSavePoint3010(remaining));
            return;
        }

        var decision = TeleportSavePointUseCase.Decide(new GameDataWorldCatalog(data), player.MapId, player.BindIndex);
        var (mapId, x, y, z, source) = (decision.MapId, decision.X, decision.Y, decision.Z, decision.Source);

        Logger.LogDebug("{Name} teleports to save point: map {Map} ({X}, {Y}, {Z}) from {Source}",
            player.Name, mapId, x, y, z, source);

        bool warped = false;
        try
        {
            // Checked before anything goes out: the departure frames without a move would take the player off its
            // own client for nothing.
            if (!decision.MapKnown)
            {
                Logger.LogWarning("{Name}: save point map {Map} is not in Map_List; not teleported", player.Name, mapId);
                return;
            }

            // Everything either form reads is loaded first, so a missing row sends nothing and gives the cooldown back.
            var loaded = await WarpEntryBurst.PrepareAsync(player, services, Logger, ct);
            if (loaded is null)
            {
                Logger.LogWarning("{Name}: character {Id} not found; not teleported", player.Name, player.CharacterId);
                return;
            }
            // From the first frame on, the escape has happened and the cooldown stands.
            warped = true;
            if (!TeleportSavePointUseCase.IsInPlace(mapId, player.MapId))
                await WarpEntryBurst.SendAsync(session, player, loaded, mapId, x, y, z, services, Logger, ct);
            else
                await WarpEntryBurst.SendInPlaceAsync(session, player, loaded, x, y, z, services, Logger, ct);
        }
        finally
        {
            if (!warped) EscapeCooldown.Release(player.CharacterId, nowUnix, previousEscape);
        }
    }

    /// <summary>
    /// Where a PC goes when it returns to its save point: 1677 here, and a type-0 revival (1372).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules, in this order:
    /// </para>
    /// <list type="number">
    /// <item>The current map's own return point, when Map_List gives one (Map_ID != 0; the WoE
    /// fortresses and the instanced dungeons): that row's (Map_ID, x, y, z), e.g. 3101 -> 1100 (610, 969, 43),
    /// 3201 -> 1300 (356, 147, 12.5), 9103 -> 1100 (725, 796, 55.5). The return point takes precedence over
    /// the bind.</item>
    /// <item>The bind point, <see cref="PlayerState.BindIndex"/> as a Position_Info id (the index 1309
    /// sets).</item>
    /// <item>No bind: the new-character spawn (1101, 209.3, 269.8, 47.7). This server's spawn constant
    /// (<see cref="WorldEntryService.DefaultMapId"/>, 209.34/268.79/48.23) is the same spot to within one
    /// unit, so the existing constant is reused instead of adding a second.</item>
    /// </list>
    /// </remarks>
    internal static (uint MapId, float X, float Y, float Z, string Source) ResolveSavePoint(
        PlayerState player, GameDataStore data)
        => WarpDestinationRules.ResolveSavePoint(player.MapId, player.BindIndex, new GameDataWorldCatalog(data));
}
