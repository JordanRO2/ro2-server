using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handler for C2S RMI 1371 (ReqPCTeleport) - the move through a gate NPC.
/// </summary>
/// <remarks>
/// <para>
/// Request layout: body+0 marker, body+4 msgTag(58086), body+6 self PC handle (0x11), body+10 u32
/// gate NPC handle (low byte 0x13), body+14 u32 option (builder blob+8/12). The frame is always 18 bytes.
/// There is no map id on the wire. (The old Skip(4)+ReadUInt32 read the msgTag 58086 as a "mapId".)
/// The option is 12 or 20.
/// </para>
/// <para>
/// The option is the gate NPC's NPCInfo Function_Type, and the two types
/// read their Function_ID from different tables.
/// </para>
/// <para>
/// OPTION 12: A Position_Info ROW. NPCInfo[gate].Function_Type_N == 12, then Position_Info[Function_ID_N]
/// gives (Area_ID, X, Y, Z), the destination of the 2311 that follows. 55 NPCs in NPCInfo.json have a
/// type-12 function, and 53 of them point at a Position_Info row on a map in Map_List. Examples: gate NPC
/// 31000000 (Function_ID_1 350), Position 350 is map 1100 (725, 796, 55.5); NPC 12040000 -> Position 87 =
/// map 1100 (1105, 677, 42); NPC 12030000 -> Position 86 = map 4104 (59, 131, 4).
/// </para>
/// <para>
/// OPTION 20: A DUNGEON GROUP. The Function_ID is a DungeonInfo DungeonGroupID, not a Position_Info id.
/// NPCInfo has 5 rows with Function_Type 20. Three are listed in function_npc_icon.json as warp NPCs to
/// a named dungeon, and in all three the Function_ID is that dungeon's DungeonGroupID: 12050000 Arena Guard
/// -> 11 (group 11 = arena), 15000000 "boatman to the Cursed Ghost Ship" -> 8, 25220000 "boatman to the
/// Sea God's Temple" -> 4. The other two are DungeonGroupIDs as well: 25300000 Deep Sea Turtle -> 4,
/// 11004309 Kirkena -> 43 (Event Zone). Read as Position_Info ids the same numbers give unrelated places
/// (Position 11 is Prontera's south gate, Position 8 a cave portal on 1103, Position 4 the culvert save
/// point on 1101). For the Arena Guard the destination is map 410303 at (59, 131, 4), which is
/// Position_Info[183], the Position_ID of the DungeonInfo row with DungeonGroupID 11 and Map_Level 3
/// (DungeonID 26).
/// </para>
/// <para>
/// WHICH LEVEL. A group has one DungeonInfo row per Map_Level (group 11: levels 1, 2, 3), and 1371
/// carries no level. The level is the one the last 2760 NfyDungeonLevelSelectInfo offered this player for
/// that group, the same record the 1531 handler uses
/// (<see cref="DungeonLevelSelectInfoNotify.TryGetOfferedLevel"/>). With no offer for the group the level is
/// unknown, so this handler logs and does not move the player instead of picking a level.
/// </para>
/// <para>
/// REPLY. There is no 1371-specific reply. The answer is 2938 <c>[01 00]</c> and then a 2505 OnPC_Out of the
/// player's own handle (2598 <c>[00000000]</c> just before them), followed by the move. The move is usually an
/// in-connection 2311 map load (with 2558 before it and 2472/2523/2522 after it). An instance move would
/// instead be a 2556 AnsChannelMove server move: the connection closes and the client logs in on
/// a new zone connection (1421 and 1417, then 2310).
/// </para>
/// <para>
/// WHAT THIS SERVER SENDS. <see cref="WarpDeparture.DepartAsync"/> (the Warp_Handling 0 effect removals, the ride
/// drop, the pyramid close, 2938 <c>[01 00]</c>, the self 2505 and the kept effects' 2499 runs), then
/// <see cref="WarpService.WarpAsync"/>, for both options. The departure order is: the PC's Warp_Handling 0
/// effects are removed ahead of the 2938; a riding PC drops the ride after those removals (2497, then effect 100
/// and in one case 62006, then 2712); an armed Illusion Pyramid is closed (2909); then 2938 <c>[01 00]</c> and
/// the self 2505; the kept effects' runs (2499 lines, then a 2498) follow, all Warp_Handling 1 and each naming an
/// Effect_Table (effect, Trait_Kind, Task_Type) row; and 2558 2743 2595 2498 2669 end right before the 2311.
/// WarpService.WarpAsync sends no 2506 or 2507 before the 2311 and sends 2472 2523 2522 and the collection trio
/// after it. GateRequestIsRetails1371Window (WarpDepartureTests) exercises this handler.
/// MultiTeleportNpcHandler (1615) sends the same departure; the move after it is the same-server 2311 that
/// DungeonLevelSelectHandler (1521), PartyDungeonTeleportHandler (1531) and the /tp, Kafra and dungeon paths also
/// use. This server has no instance channels, so the move is never a 2556, including the option-20 dungeon entry.
/// Not reproduced: 2598 <c>[00000000]</c> before the 2938 (a ProudNet P2P-group message); the 2315/2497 that
/// accompany the removal of a visible or speed effect (EffectService's removal sends 2498/2493/2494 only); and
/// a self 2398 that sometimes follows the self 2505. Map_Min_Level, Need_Quest_ID and Enter_Count
/// are not enforced for option 20.
/// </para>
/// </remarks>
public class WarpHandler : ZoneRmiHandlerBase<WarpHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTeleport;
    public override string Name => "RequestWarpById";

    /// <summary>
    /// The 1371 option, and NPCInfo Function_Type, whose Function_ID is a Position_Info id (see "OPTION 12"
    /// on the class).
    /// </summary>
    internal const uint TeleportGateFunctionType = 12;

    /// <summary>
    /// The 1371 option, and NPCInfo Function_Type, whose Function_ID is a DungeonInfo DungeonGroupID (see
    /// "OPTION 20" on the class).
    /// </summary>
    internal const uint DungeonGroupFunctionType = 20;

    /// <summary>Message tag (envelope offset +4) of a 1371 request.</summary>
    internal const ushort MsgTagReqPCTeleport = 58086;

    /// <summary>Length of a 1371 request body: envelope and self PC handle (10), gate NPC handle (4), option (4).</summary>
    private const int RequestLength = 18;

    /// <summary>Offset of the gate NPC handle in the request body, after the envelope and the self PC handle.</summary>
    private const int GateHandleOffset = 10;

    /// <summary>Window within which a second accepted 1371 of the same PC is ignored, in seconds (matches the 1528 guard).</summary>
    private const double DedupeSeconds = 3.0;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerState, long[]> LastWarpTicks = new();

    /// <summary>True and stamps the PC when no accepted 1371 of it falls inside the dedupe window.</summary>
    private static bool TryClaimWindow(PlayerState player)
    {
        var last = LastWarpTicks.GetValue(player, _ => new long[1]);
        lock (last)
        {
            var now = DateTime.UtcNow.Ticks;
            if (last[0] != 0 && now - last[0] < TimeSpan.FromSeconds(DedupeSeconds).Ticks) return false;
            last[0] = now;
            return true;
        }
    }

    public WarpHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<WarpHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (payload.Length < RequestLength) return; // envelope+handle + u32 gate handle + u32 option
        var reader = new PacketReader(payload.Span);
        reader.Skip(GateHandleOffset);
        var gateHandle = reader.ReadUInt32();
        var gateOption = reader.ReadUInt32();

        // HSV MV-7: a dead PC takes no gate (log-and-ignore, no 2938).
        if (player.IsDead) return;

        // The gate must be an NPC this server spawned on the player's current map. A handle from
        // another map (or none at all) is refused rather than guessed at.
        var gate = services.GetRequiredService<NpcStateManager>().GetNpc(gateHandle);
        if (gate is null || gate.MapId != player.MapId)
        {
            Logger.LogWarning("Player {Name} used teleport gate {Gate:X8} option {Option}, which is not an NPC on map {Map}",
                player.Name, gateHandle, gateOption, player.MapId);
            return;
        }

        // HSV MV-7: the interaction radius is not enforced; a check on it refused a legitimate 1371
        // (WarpDepartureTests.GateRequestIsRetails1371Window), so it only logs.
        if (!NpcInteraction.GateInReach(player, gate, NpcInteraction.DefaultInteractRadius))
            Logger.LogDebug("Player {Name} used teleport gate {Gate:X8} from beyond the unverified interaction radius; allowed", player.Name, gateHandle);

        var data = services.GetRequiredService<GameDataStore>();
        PositionData dest;
        switch (gateOption)
        {
            case TeleportGateFunctionType:
                if (!TryResolveFunctionPosition(data, gate.NpcId, TeleportGateFunctionType, out dest))
                {
                    Logger.LogWarning("Player {Name} used gate NPC {NpcId} option {Option}: no function of that type with a Position_Info row",
                        player.Name, gate.NpcId, gateOption);
                    return;
                }
                break;
            case DungeonGroupFunctionType:
                if (!TryResolveDungeonGroupEntry(data, player, gate.NpcId, out dest, out var dungeonRow))
                    return; // logged inside
                // HSV MV-7: Map_Min_Level and Need_Quest_ID of the offered row, as 1521 enforces them. The protocol
                // defines no refusal for a 1371, so it is log-and-ignore.
                if (player.Level < dungeonRow.Map_Min_Level)
                {
                    Logger.LogInformation("Player {Name} (level {Level}) used dungeon gate NPC {NpcId}: below Map_Min_Level {Min} of DungeonID {Dun}; ignored",
                        player.Name, player.Level, gate.NpcId, dungeonRow.Map_Min_Level, dungeonRow.DungeonID);
                    return;
                }
                if (dungeonRow.Need_Quest_ID != 0
                    && !(await services.GetRequiredService<IQuestRepository>().GetCompletedQuestIdsAsync(player.CharacterId))
                        .Contains(dungeonRow.Need_Quest_ID))
                {
                    Logger.LogInformation("Player {Name} used dungeon gate NPC {NpcId}: quest {Quest} of DungeonID {Dun} not completed; ignored",
                        player.Name, gate.NpcId, dungeonRow.Need_Quest_ID, dungeonRow.DungeonID);
                    return;
                }
                // Enter_Count is NOT enforced: 1371 does not count entries (1521/1531 do), so a limit check without
                // the count would be half a rule. BLOCKED, flagged.
                break;
            default:
                // 12 and 20 are the only known options.
                Logger.LogWarning("Player {Name} used gate NPC {NpcId} option {Option}, which no capture shows, not moved",
                    player.Name, gate.NpcId, gateOption);
                return;
        }

        // Check the map before the 2938/2505 go out: sending them without a move would despawn the player
        // from their own client for nothing. WarpAsync would refuse the same map after they were sent.
        if (data.GetMap(dest.Area_ID) is null)
        {
            Logger.LogWarning("Gate NPC {NpcId} option {Option} points at map {Map}, which is not in Map_List, not moved",
                gate.NpcId, gateOption, dest.Area_ID);
            return;
        }

        // The departure (WarpDeparture.DepartAsync): the Warp_Handling 0 effects removed, the ride dropped, an open
        // Illusion Pyramid settled and closed (2909 [0][1]), then 2938 [01 00] and
        // the self 2505, then the kept effects' 2499 runs. WarpAsync below then finds the ride dropped and nothing
        // left to close.
        // HSV MV-7: re-entrancy dedupe, claimed only once every gate above passed so a refusal does not consume it.
        // The 3 s window mirrors the 1528 guard.
        if (!TryClaimWindow(player))
        {
            Logger.LogDebug("Player {Name} duplicate 1371 within {Sec}s; ignored", player.Name, DedupeSeconds);
            return;
        }

        await WarpDeparture.DepartAsync(session, player, services, Logger, ct);

        var ok = await WarpService.WarpAsync(session, player, dest.Area_ID,
            dest.Position_X, dest.Position_Y, dest.Position_Z, services);
        Logger.LogInformation("Player {Name} took gate NPC {NpcId} option {Option} to map {Map} ({X}, {Y}, {Z}): warped={Ok}",
            player.Name, gate.NpcId, gateOption, dest.Area_ID, dest.Position_X, dest.Position_Y, dest.Position_Z, ok);
    }

    /// <summary>
    /// Option 20: the entry point of the DungeonInfo row for the gate's DungeonGroupID at the level the
    /// last 2760 offered this player (see "WHICH LEVEL" on the class). Logs and returns false when the NPC
    /// has no type-20 function, the player holds no offer for that group, or the group and level do not
    /// select exactly one row with a Position_Info entry point.
    /// </summary>
    private bool TryResolveDungeonGroupEntry(GameDataStore data, PlayerState player, uint npcId,
        out PositionData position, out DungeonInfo row)
    {
        position = null!;
        row = null!;
        if (!TryResolveFunctionId(data, npcId, DungeonGroupFunctionType, out var groupId))
        {
            Logger.LogWarning("Player {Name} used gate NPC {NpcId} option {Option}, but the NPC has no function of that type",
                player.Name, npcId, DungeonGroupFunctionType);
            return false;
        }
        if (!DungeonLevelSelectInfoNotify.TryGetOfferedLevel(player, groupId, out var level))
        {
            Logger.LogInformation("Player {Name} used dungeon gate NPC {NpcId} (group {Group}) with no 2760 offer for that group: no local capture shows which level retail enters then, not moved",
                player.Name, npcId, groupId);
            return false;
        }
        var rows = data.DungeonInfo
            .Where(d => d.DungeonGroupID == groupId && d.Map_Level == level)
            .ToList();
        if (rows.Count != 1)
        {
            Logger.LogWarning("Player {Name} used dungeon gate NPC {NpcId}: group {Group} level {Level} matches {Count} DungeonInfo rows, not moved",
                player.Name, npcId, groupId, level, rows.Count);
            return false;
        }
        if (!data.Positions.TryGetValue(rows[0].Position_ID, out var pos))
        {
            Logger.LogWarning("Player {Name} used dungeon gate NPC {NpcId}: DungeonID {Dun} has no Position_Info row {Pos}, not moved",
                player.Name, npcId, rows[0].DungeonID, rows[0].Position_ID);
            return false;
        }
        Logger.LogInformation("Player {Name}'s dungeon gate NPC {NpcId} resolves to DungeonID {Dun} (group {Group} level {Level})",
            player.Name, npcId, rows[0].DungeonID, groupId, level);
        position = pos;
        row = rows[0];
        return true;
    }

    /// <summary>
    /// The Function_ID of the first of the NPC's function slots (NPCInfo Function_Type_N / Function_ID_N,
    /// N = 1..3) whose type is <paramref name="functionType"/>. NPCInfo has only three function slots.
    /// </summary>
    internal static bool TryResolveFunctionId(GameDataStore data, uint npcId, uint functionType,
        out uint functionId)
        => WarpDestinationRules.TryResolveFunctionId(new GameDataWorldCatalog(data), npcId, functionType, out functionId);

    /// <summary>
    /// Resolves the Position_Info row an NPC's function of type <paramref name="functionType"/> points at
    /// (NPCInfo Function_Type_N / Function_ID_N, N = 1..3). Only <see cref="TeleportGateFunctionType"/>
    /// holds a Position_Info id; type 20 holds a DungeonGroupID and goes through
    /// <see cref="TryResolveDungeonGroupEntry"/> instead.
    /// </summary>
    internal static bool TryResolveFunctionPosition(GameDataStore data, uint npcId, uint functionType,
        out PositionData position)
        => WarpDestinationRules.TryResolveFunctionPosition(new GameDataWorldCatalog(data), npcId, functionType, out position);
}
