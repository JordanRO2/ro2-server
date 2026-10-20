using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1615 ReqMultiTeleportNpc — the player picked a destination from a multi-destination teleporter
/// NPC's dialogue (the fortress / guild-citadel teleporters, dialog_info Action_Type 32).
/// </summary>
/// <remarks>
/// <para>
/// BODY. A 9-byte VarBlob32, <c>[u32 length=5][u32 npcHandle][u8 slot]</c> (the handle carries the 0x13 NPC tag).
/// The slot is 1..4 and is the dialogue's
/// Select_ID: every such NPC's dialog_info rows offer Select_ID 1..N with Action_Type 32.
/// </para>
/// <para>
/// THE ANSWER is 2905 AnsMultiTeleportNpc
/// <c>[u16 result]</c>: result 0, 24323 (0x5F03) or 24321 (0x5F01); 21019 and 24320 also exist. An accepted request is
/// followed by the server-driven warp: the burst ending in 2311 OnReceiveMapLoad + 2472/2523/2522, or a zone-server
/// 2556 move. No 2435 follows any of them, so there is no fare.
/// </para>
/// <para>
/// WHAT THE CODES SAY. The client does nothing on a 2905 but show a string_ui line per result code:
/// 24323 -> 11109 "Only usable by the members of the victorious guild.", 24321 -> 11108 "Entrance is not granted
/// while you are a member of a party.", 24320 -> 11110 "Entrance is not granted if you are not a member of the
/// guild." (21019 falls through to the default branch). The client moves nothing on a 2905:
/// the move is the server's 2938 / 2311 that follows.
/// </para>
/// <para>
/// DESTINATIONS. The NPC's NPCInfo function of type 39 names a destination group
/// (Function_ID_N), and every 2311 destination is a Position_Info row. Each served (NPC, slot) lands on the row
/// <see cref="MeasuredDestinations"/> holds. That group, slot and Position_Info
/// table is NOT in this server's data (no converted JSON uses those Position_IDs as destinations), so the
/// known pairs are listed in
/// <see cref="MeasuredDestinations"/> and the coordinates still come from Position_Info. The Nth row of
/// Teleport.json by Index is not used: those rows are gate destinations on maps
/// 1100–1104, 4101, 4102, 8070 and 8088, which are not the teleporter destinations (slot 1 of group 1
/// goes to map 3101, not map 8070).
/// </para>
/// <para>
/// WHAT AN ACCEPTED MOVE SENDS, in order:
/// 2905 -> the removal of the Warp_Handling 0 effects -> [the ride drop] -> 2938 [01 00] -> the self
/// 2505 -> the kept effects' 2499 runs -> 2558 2743 2595 2498 2669 -> 2311 -> 2472
/// 2523 2522 -> 2913/2919/2918 (in either order). No 2506 or 2507 comes
/// before the 2311. The sequence is
/// replayed byte for byte in WarpDepartureTests.
/// <see cref="WarpDeparture.DepartAsync"/> sends the part up to the kept-effect runs and
/// <see cref="WarpService.WarpAsync"/> the rest. Zone-server 2556 moves are not used: this
/// server has no instance channels, so every accepted move here is an in-connection 2311.
/// </para>
/// <para>
/// SIEGE-HOLDER GATE, PER DESTINATION. The 24323 refusal applies to the fortress destinations: 17260000 (group 1)
/// slots 1-4 and 31120000 (group 7) slots 1-3, among others. What decides is the requester and the slot together.
/// Each slot is a different fortress (3101's interior/east/west/south, 3201's
/// fortress/east/west, Position_Info's own labels), which a guild holds by winning its siege, and 24323 is "Only usable
/// by the members of the victorious guild.". This server keeps no siege ownership (which guild holds each
/// fortress, set by siege outcomes, with the 60037/60043 occupation effects that go with it), so it serves every
/// known pair to everyone and never sends the per-destination 24323. That is the missing system.
/// </para>
/// <para>
/// THE AGIT GROUPS, 4, 6, 10 and 11, are gated by the same ownership. Each slot leads to one fixed row,
/// the agit of one fortress: group 4 (30940001) slot 1 -> 347 on 5102 and slot 2 -> 350, the Prontera
/// guild-house exit; group 6 (30940003) slot 1 -> 349 on 5104; group 10 (31520000) slot 2 -> 368
/// on 5302; group 11 (31470000) slot 2 -> 369 on 5303. Serving them without ownership would open every agit
/// to every player, and 24323 is the answer the client's string table gives a non-member. Also left out: groups 3, 5,
/// 13 and 14, and 9 (31420000 on 3201), whose handle no 2506/2317/2319 of the session names (such moves
/// on 3201 go to 367, the 5301 agit, which fits it, unconfirmed). The 24321
/// "party member" refusal is not tied to known destinations, so no
/// request is refused for a party here.
/// </para>
/// <para>
/// A request that cannot be resolved (the handle is not an NPC on the player's map, the NPC has no type-39 function, or
/// its (group, slot) is not in <see cref="MeasuredDestinations"/>) is refused with 24323.
/// </para>
/// </remarks>
public class MultiTeleportNpcHandler : ZoneRmiHandlerBase<MultiTeleportNpcHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMultiTeleportNpc;
    public override string Name => "MultiTeleportNpc";

    /// <summary>
    /// Length of a 1615 request: the VarBlob32 length prefix (4), the NPC handle (4) and the slot (1). A shorter body is refused.
    /// </summary>
    private const int RequestLength = 4 + 4 + 1;

    /// <summary>Slots are numbered from 1: the slot is the dialogue's Select_ID, and slot n selects entry n-1 of a group.</summary>
    private const int FirstSlot = 1;

    /// <summary>2905 result: accepted.</summary>
    internal const ushort ResultAccepted = 0;

    /// <summary>
    /// 2905 result 24323 (0x5F03): string_ui 11109 "Only usable by the
    /// members of the victorious guild." (see WHAT THE CODES SAY on the class). The other refusal, 24321
    /// (0x5F01, 11108 "Entrance is not granted while you are a member of a party."), is not sent here (see THE AGIT
    /// GROUPS).
    /// </summary>
    internal const ushort ResultRefused = 24323;

    /// <summary>NPCInfo Function_Type of the multi-destination teleporter; its Function_ID is the group.</summary>
    internal const int MultiTeleportFunctionType = 39;

    /// <summary>
    /// Destination group (NPCInfo Function_ID of the type-39 function) → Position_Info id per 1-based
    /// slot:
    /// <list type="bullet">
    /// <item>1 (NPC 17260000, map 1100): 274, 325, 326, 327 — fortress 3101.</item>
    /// <item>2 (NPC 28950000, map 1100): 275, 276 — fortress 3101 west/east gate.</item>
    /// <item>7 (NPC 31120000, map 1300): 370, 371, 372 — fortress 3201. Slot 3 is gated by siege ownership
    /// (see the class).</item>
    /// <item>8 (NPC 31130000, map 1300): 378, 365 — fortress 3201.</item>
    /// <item>12 (NPCs 31030000/31030001, the citadels): 381, 383, 384, 385
    /// — map 9103.</item>
    /// </list>
    /// The order also matches Position_Info's own labels (group 1: interior, east, west, south
    /// fortress; group 12: the 7, 6, 9 and 3 o'clock entrances).
    /// </summary>
    internal static readonly IReadOnlyDictionary<uint, uint[]> MeasuredDestinations = new Dictionary<uint, uint[]>
    {
        [1] = new uint[] { 274, 325, 326, 327 },
        [2] = new uint[] { 275, 276 },
        [7] = new uint[] { 370, 371, 372 },
        [8] = new uint[] { 378, 365 },
        [12] = new uint[] { 381, 383, 384, 385 },
    };

    public MultiTeleportNpcHandler(IServiceScopeFactory s, ILogger<MultiTeleportNpcHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) { await AckAsync(session, ResultRefused); return; }
        var reader = new PacketReader(payload.Span);
        reader.ReadUInt32();                 // VarBlob32 length (5)
        var npcHandle = reader.ReadUInt32(); // the teleporter NPC's actor handle
        var slot = reader.ReadByte();        // dialog Select_ID, 1-based

        // A dead PC is refused (24323).
        if (player.IsDead)
        {
            Logger.LogInformation("{Name} used teleporter {Handle:X8} slot {Slot} while dead; refused", player.Name, npcHandle, slot);
            await AckAsync(session, ResultRefused);
            return;
        }

        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);
        // The NPC must be one this server tracks on the player's map (hard gate). The distance part of
        // GateInReach is a loose guard: the interaction radius is not enforced strictly.
        // Siege-holder / party refusal stays unimplemented (no siege system; see the class).
        if (npc is null || npc.MapId != player.MapId)
        {
            Logger.LogInformation("{Name} used teleporter {Handle:X8} slot {Slot}, which is not an NPC on map {Map}; refused",
                player.Name, npcHandle, slot, player.MapId);
            await AckAsync(session, ResultRefused);
            return;
        }
        if (!NpcInteraction.GateInReach(player, npc, NpcInteraction.DefaultInteractRadius))
            Logger.LogDebug("{Name} used teleporter {Handle:X8} slot {Slot} from beyond the unverified interaction radius; allowed",
                player.Name, npcHandle, slot);

        var data = services.GetRequiredService<GameDataStore>();
        if (!TryResolveDestination(data, npc.NpcId, slot, out var dest))
        {
            Logger.LogInformation("{Name} used teleporter NPC {NpcId} slot {Slot}: no measured destination for it; refused",
                player.Name, npc.NpcId, slot);
            await AckAsync(session, ResultRefused);
            return;
        }

        // Ack first, then the departure, then the warp: the 2905 precedes everything else of an accepted
        // request, and the departure (the Warp_Handling 0 removals, the ride drop, the pyramid close, 2938 [01 00], the
        // self 2505 and the kept-effect runs) comes before the 2558 burst and the 2311 WarpAsync sends (see the class).
        await AckAsync(session, ResultAccepted);
        await WarpDeparture.DepartAsync(session, player, services, Logger, ct);
        var ok = await WarpService.WarpAsync(session, player,
            dest.Area_ID, dest.Position_X, dest.Position_Y, dest.Position_Z, services);

        Logger.LogInformation("{Name} teleporter NPC {NpcId} slot {Slot} -> Position_Info {Pos} (map {Map} {X:F1},{Y:F1},{Z:F1}): warped={Ok}",
            player.Name, npc.NpcId, slot, dest.Position_ID, dest.Area_ID, dest.Position_X, dest.Position_Y, dest.Position_Z, ok);
    }

    /// <summary>
    /// The Position_Info row slot <paramref name="slot"/> of NPC <paramref name="npcId"/> leads to:
    /// the NPC's type-39 function gives the group, <see cref="MeasuredDestinations"/> the row. False
    /// when any link is missing, including a destination map absent from Map_List.
    /// </summary>
    internal static bool TryResolveDestination(GameDataStore data, uint npcId, byte slot, out PositionData destination)
    {
        destination = null!;
        if (!data.Npcs.TryGetValue(npcId, out var info)) return false;

        uint? group = null;
        foreach (var (type, id) in new[]
                 {
                     (info.Function_Type_1, info.Function_ID_1),
                     (info.Function_Type_2, info.Function_ID_2),
                     (info.Function_Type_3, info.Function_ID_3),
                 })
        {
            if (type == MultiTeleportFunctionType) { group = id; break; }
        }

        if (group is not uint g || !MeasuredDestinations.TryGetValue(g, out var slots)) return false;
        if (slot < FirstSlot || slot > slots.Length) return false;
        if (!data.Positions.TryGetValue(slots[slot - FirstSlot], out var pos)) return false;
        if (data.GetMap(pos.Area_ID) is null) return false;

        destination = pos;
        return true;
    }

    /// <summary>AnsMultiTeleportNpc 2905 — bare 2-byte [u16 result].</summary>
    private static Task AckAsync(ProudSession session, ushort result)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsMultiTeleportNpc, w => w.WriteUInt16(result));
}
