using RO2.Application.Interfaces;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1531 ReqPartyDungeonTeleport — enter an instance dungeon. The C2S body is a fixed 4 bytes
/// <c>[u32]</c>, and that u32 is a DungeonInfo <b>DungeonGroupID</b>, not a DungeonID. A party member sends it when
/// it accepts the 2760 NfyDungeonLevelSelectInfo [u32 group][u16 level] its leader's 1521 raised.
///
/// DESTINATION. The request value selects the DungeonInfo row with
/// DungeonGroupID == the request and Map_Level == the level the server offered in the 2760 just before
/// (for example 42/L3 -> 426103, 42/L1 -> 426101, 13/L2 -> 425102, 7 and 9 at L3 -> 421103 / 423103), standing at
/// Position_Info[Position_ID], on its Area_ID: the 2556 destination block and the 2310 after the reconnect both
/// carry it (<see cref="ResolveArrival"/>). The old code read the value as a DungeonID, which sends
/// group 42 to DungeonID 42 = map 4116, a different dungeon. The level is the one the last 2760 offered this player
/// for that group: the leader's 1521 sends it through DungeonLevelSelectInfoNotify.SendToPartyAsync, which records
/// it per member, and <see cref="DungeonLevelSelectInfoNotify.TryGetOfferedLevel"/> returns it here. With no offer
/// for the group (the player was never offered it) the lowest Map_Level is used, since there is no offered level to
/// honour.
///
/// THE MOVE is a channel hand-off, not a map load. The answer comes 180-440 ms later with [the view-leave
/// burst 2505/2507/2513/1106, not sent], [the departure removals], 2598 [00000000], 2938 [01 00], a self 2505 and the
/// 81-byte 2556 AnsChannelMove whose destination block names the arrival, and the client reconnects (1421 -> 2557 ->
/// 1417 -> 2310 on the dungeon map).
/// The departure removals are the member's Warp_Handling-0 effects and its ride (WarpDeparture.DropWarpEffectsAsync,
/// RidingService.DropAsync), as the leader's 1521 and the finder's 1559 already send them: the view-leave burst
/// can be followed by 2498, 2315, 2497, 2493 and the 2494 of effect 60037
/// (Effect_Table Warp_Handling 0), body carrying the member's handle; in some cases
/// the 2909 Pyramid close follows directly. A member with no such
/// effect and no ride gets none (view-leave -> 2598 -> 2938 -> self 2505).
/// This server does not send the 2315/2497 refresh for a visible/speed effect such as 60037 (WarpDeparture's
/// remarks). The 2556 destination
/// (+56 map, +60/64/68 xyz, +72 direction) matches <see cref="ResolveArrival"/>.
/// <see cref="DungeonChannelHandOff.EnterAsync"/> sends that, 2598 included (<c>leaveP2PGroup</c>), on this server's
/// own listener and channel. This server used to send a same-connection 2311 (WarpService.WarpAsync), with no 2938 and no self 2505,
/// a shape that is not used for 1531.
///
/// THE INSTANCE AND THE ENTRY COUNT. For an instance-dungeon row (Map_List.Map_Type 1) the member
/// enters its party's instance of that DungeonID (<see cref="DungeonInstanceRegistry"/>, the one its
/// leader's 1521 opened; a member with no party owns its own), and the entry counts toward DungeonInfo.Enter_Count
/// (<see cref="IDungeonEntryCountRepository"/>, the 'used' of 3035) when the member had not been counted in that
/// instance. Past the limit the answer is the lone 2556 19015 (<see cref="DungeonEntryGate"/>). The level and quest checks of 1521 are not run here. A 1534 of the leader resets the instance, and the member's next 1531
/// opens a new one, counted again. The Training Ground (group 21, map 4110, Map_Type 5) is the same hand-off with no
/// registry instance, as the leader's 1521 treats it (DungeonEntryGate): the answer to 1531 [15000000] is a
/// result-0 2556 to map 4110 at 100/100/100 dir 0, the row (21, offered level 1)'s Position_Info 290.
/// Still not modelled: per-party copies of the map, Bind_Value, the instance channel
/// handle in 2557, and the 19010 refusal (map 0; string 10061 "Cannot enter
/// dungeon while party members are in combat in dungeon"), which needs the instance's combat state.
/// </summary>
public class PartyDungeonTeleportHandler : ZoneRmiHandlerBase<PartyDungeonTeleportHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDungeonTeleport;
    public override string Name => "PartyDungeonTeleport";

    /// <summary>C2S 1531 body length: a single u32 DungeonInfo DungeonGroupID.</summary>
    private const int RequestBodyLength = 4;

    public PartyDungeonTeleportHandler(IServiceScopeFactory s, ILogger<PartyDungeonTeleportHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength) return;
        if (!player.MapLoaded)
        {
            // A 1531 after this connection's hand-off has gone out (it leaves MapLoaded false), or before a map-load
            // ack. A second hand-off would issue a second ticket and revoke the one the first 2556 carries.
            Logger.LogDebug("{Name} sent a 1531 with no loaded map, ignored", player.Name);
            return;
        }
        var reader = new PacketReader(payload.Span);
        var groupId = reader.ReadUInt32();

        var data = services.GetRequiredService<GameDataStore>();
        ushort? offered = DungeonLevelSelectInfoNotify.TryGetOfferedLevel(player, groupId, out var offeredLevel)
            ? offeredLevel
            : null;
        if (ResolveArrival(data, groupId, offered) is not var (dungeon, arrival))
        {
            // No reply is defined for a refused 1531, and 2348 is the invitation target's answer to a 1383, not a
            // dungeon error. Checked before anything is sent, so a failed lookup cannot despawn the player from its
            // own client for nothing.
            Logger.LogInformation("{Name} tried to enter dungeon group {Group} with no resolvable arrival point",
                player.Name, groupId);
            return;
        }

        // The party's instance and the entry count, for an instance-dungeon row (see the summary).
        var isInstance = data.GetMap(arrival.Area_ID)?.Map_Type == DungeonEntryGate.InstanceMapType;
        DungeonInstanceEntry? entry = null;
        IDungeonEntryCountRepository? counts = null;
        if (isInstance)
        {
            var party = services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId);
            var owner = DungeonInstanceOwner.For(party, player.CharacterId);
            var registry = DungeonInstanceRegistry.Shared;
            var wouldCount = registry.WouldCount(owner, dungeon.DungeonID, player.CharacterId);
            if (dungeon.Enter_Count != 0)
            {
                counts = services.GetRequiredService<IDungeonEntryCountRepository>();
                var used = wouldCount ? await counts.GetUsedAsync(player.CharacterId, dungeon.DungeonID, DateTime.UtcNow, ct) : 0;
                var refusal = DungeonEntryGate.CheckEnterCount(dungeon, wouldCount, used);
                if (refusal != 0)
                {
                    await DungeonEntryGate.RefuseAsync(session, session.AccountId ?? player.AccountId, refusal);
                    Logger.LogInformation("{Name} tried to enter dungeon group {Group} (DungeonID {Dun}) with {Used}/{Limit} entries used: refused {Result}",
                        player.Name, groupId, dungeon.DungeonID, used, dungeon.Enter_Count, DungeonEntryGate.ResultEnterCountExceeded);
                    return;
                }
            }
            entry = registry.Enter(owner, dungeon.DungeonID, arrival.Area_ID, player.CharacterId);
        }

        Logger.LogInformation("{Name} enters dungeon group {Group} (DungeonID {Dun}, level {Level}) -> Position_Info {Pos} map {Map}{Instance}",
            player.Name, groupId, dungeon.DungeonID, dungeon.Map_Level, dungeon.Position_ID, arrival.Area_ID,
            entry is { } e ? $" ({(e.IsNewInstance ? "opened" : "re-entered")} instance {e.InstanceId})" : "");
        // The departure removals go after the entry-count gate (a 19015 refusal is a lone 2556 with none) and before
        // the hand-off: EnterAsync moves MapId to the arrival, and the 2494 / 2497 / 2712 removals must go to the map
        // being left. Both send nothing when there is nothing to drop.
        await WarpDeparture.DropWarpEffectsAsync(player, data, services.GetRequiredService<EffectService>());
        await RidingService.DropAsync(session, player, services);

        // leaveP2PGroup: the 1531 answer carries 2598 [u32 0] before its 2938.
        await DungeonChannelHandOff.EnterAsync(session, player, arrival, services, Logger, ct, leaveP2PGroup: true);

        if (entry is { CountsForCharacter: true } && counts is not null)
            await DungeonLevelSelectHandler.CountEntryAsync(counts, player, dungeon, Logger, ct);
    }

 /// <summary>
 /// The DungeonInfo row a 1531 for <paramref name="groupId"/> enters and its arrival point: the row of the level the
 /// 2760 offered (<paramref name="offeredLevel"/>), else the group's lowest Map_Level; the arrival is
 /// Position_Info[row.Position_ID] ON ITS OWN MAP (Area_ID), exactly as the leader's 1521 (DungeonLevelSelectHandler)
 /// resolves it. Map_Index1 is not the arrival map: it differs from Position_Info[Position_ID].Area_ID in 10 of the
 /// 81 DungeonInfo rows (DungeonIDs 55-61, 71, 74, 78), and moving there put a member who accepted the offer on
 /// another map than its leader. Null when the group has no row, the row's Position_ID has no Position_Info, or its
 /// map is not in Map_List.
 /// </summary>
    internal static (DungeonInfo Row, PositionData Arrival)? ResolveArrival(GameDataStore data, uint groupId, ushort? offeredLevel)
    {
        var dungeon = offeredLevel is ushort level
            ? data.DungeonInfo.FirstOrDefault(d => d.DungeonGroupID == groupId && d.Map_Level == level)
            : null;
        dungeon ??= data.DungeonInfo
            .Where(d => d.DungeonGroupID == groupId)
            .OrderBy(d => d.Map_Level)
            .FirstOrDefault();
        if (dungeon is null
            || !data.Positions.TryGetValue(dungeon.Position_ID, out var pos)
            || data.GetMap(pos.Area_ID) is null)
            return null;
        return (dungeon, pos);
    }
}
