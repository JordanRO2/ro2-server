using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1372 ReqPCRevival - Player resurrection</summary>
/// <remarks>
/// <para>
/// Request layout, 11 bytes:
/// <c>[compactLen 01 09][u16 innerSize=9][u16 msgTag=58088][u32 actorHandle][u8 reviveType]</c>, so the
/// revive type is blob+8 = body+10. Type 0 is the save-point return, type 2 the WoE save location.
/// </para>
/// <para>
/// TYPE 0 = RETURN TO THE SAVE POINT. The server answers with 2399 OnRevival in its
/// no-position form (<c>[handle][handle][00 00 00][zero x/y/z]</c>), then the map-change burst
/// ending in 2311 at the destination <see cref="TeleportSavePointHandler.ResolveSavePoint"/> computes.
/// The PC is not revived in place with a position-carrying 2399, which is not what type 0 means.
/// </para>
/// <para>
/// TYPE 2 = RESURRECT AT THE WoE / KAFRA-SEASONING SAVE LOCATION, which this server does not have, so it
/// is always refused with a self 2399 carrying result code 24333 (<c>0d 5f</c> at blob+12), which the client
/// shows as string 11301, "Cannot use your WoE save location due to the disapperance of Kafra seasoning."
/// (data/strings/string_ui.json). The code names the location, not the PC's state, so the refusal goes out
/// dead or not. A dead PC is not revived in place at full HP/SP for it, which would skip the save-point
/// return and the half-HP penalty of type 0.
/// </para>
/// <para>
/// TYPE 1 (and any other value) has no established menu option. The client's
/// other revival options cost an item or are rate-limited (string_ui 714 "Resurrect On the Spot (Use
/// Spinel)", 10516 "... Able to use Spinel again in 10 mins", 15205 "Able to %d Resurrect in place per
/// day"), none of which this server models, so it gets no reply rather than a free in-place refill. The
/// 2399 is what hides the death window, so the player can still pick the save point. The skill revival of 1684
/// is a different message (<see cref="RevivalSkillHandler"/>): full HP/SP at the caster's position, which only a
/// 3016 offer this server sent can unlock.
/// </para>
/// <para>
/// 2399 is not only for the reviver: onlookers get it too. The revival 2399 therefore also goes to nearby
/// PCs (BroadcastToNearbyAsync skips the sender, who gets its own copy first).
/// </para>
/// <para>
/// It is followed by a state-0 2398 OnChangeState for the reviver, to the same recipients, which
/// clears the dead state 6 the lethal hit set (see <see cref="SendNormalStateAsync"/>).
/// </para>
/// <para>
/// ONLY A DEAD PC IS REVIVED. A request from a PC that is not dead (no 2391 OnDeadMe since its 2310, not
/// undone by a self 2399) gets no revival, no 2398 and no heal. A request while the client is loading a map
/// is dropped (a session's RMIs are handled one at a time, and <see cref="WarpService.WarpAsync"/> clears
/// <see cref="PlayerState.MapLoaded"/> before its 2311, so the duplicate the client fires right after a
/// type-0 revival lands here and cannot start a second warp). A type-0 request from a PC that is not dead
/// gets a result-code 2399 (code 1; non-zero codes are shown by the client as a system message); type 2
/// always gets its 24333, and type 1 nothing at all (see above). The reply is unicast.
/// </para>
/// <para>
/// HP/SP: 2399 has no vitals field; a type-0 revival comes back at HALF of both, floored. The revival sends a
/// self 2401 right after the 2398, so the client's bars leave 0 at once: the 2311-form sequence is
/// 2399 -> 2398 -> 2401 -> 2311. The 2315 sent after the revival carries SP == max/2 and HP in
/// [max/2, 0.52*max]; its max may leave out an equipment bonus.
/// </para>
/// </remarks>
public class RevivalHandler : ZoneRmiHandlerBase<RevivalHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCRevival;
    public override string Name => "Revival";

    /// <summary>msgTag of 2399 OnRevival: 58089 (0xE2E9), the same value ActorStatePackets writes.</summary>
    private const ushort TagOnRevival = 58089;

    /// <summary>Revive type that sends the PC to its save point.</summary>
    private const byte ReviveAtSavePoint = 0;

    /// <summary>Revive type 2, resurrection at the WoE / Kafra-seasoning save location (see the class
    /// remarks).</summary>
    private const byte ReviveAtSeasoningLocation = 2;

    /// <summary>blob+12 result code of the 2399 answering a type-0 request from a PC that is not dead:
    /// 1 (see the class remarks).</summary>
    private const ushort NotDeadResultType0 = 1;

    /// <summary>blob+12 result code of the 2399 answering a type-2 request: 24333 (0x5F0D), which the client
    /// maps to string 11301, "Cannot use your WoE save location due to the disapperance of Kafra seasoning."
    /// (data/strings/string_ui.json).</summary>
    private const ushort SeasoningLocationUnavailable = 24333;

    /// <summary>blob+19 of the state-0 2398 that follows a 2399: 2 (see
    /// <see cref="SendNormalStateAsync"/>), the same value the dead form and the sit/stand form carry.</summary>
    private const uint RevivedStateTrailer = 2;

    /// <summary>Length of the ReqPCRevival blob: <c>[u16 innerSize=9][u16 msgTag][u32 actorHandle][u8 reviveType]</c>.
    /// Also the minimum innerSize the request may declare.</summary>
    private const int RequestBlobLength = 9;

    /// <summary>Offset of the u8 revive type inside the request blob (body+10 with the 2-byte compact prefix).</summary>
    private const int ReviveTypeBlobOffset = 8;

    /// <summary>A type-0 revival restores 1/2 of max HP and max SP, floored.</summary>
    private const int ReviveVitalsDivisor = 2;

    /// <summary>Lower bound of the revived HP: keeps the PC out of the <c>Hp &lt;= 0</c> dead test.</summary>
    private const int MinRevivedHp = 1;

    /// <summary>blob+12 result code of a 2399 that revives (any non-zero code is an error shown by the client).</summary>
    private const ushort ResultRevived = 0;

    /// <summary>blob+14 of a 2399 with no position block: the flag byte is 0 and the x/y/z floats are zero.</summary>
    private const byte NoPositionFlag = 0;

    /// <summary>Value of each x/y/z float of a 2399 without a position (the destination comes in the 2311).</summary>
    private const float NoCoordinate = 0f;

    /// <summary>Target handle of the state-0 2398 that follows a revival: none (blob+23 is zero).</summary>
    private const uint NoTargetHandle = 0;

    public RevivalHandler(IServiceScopeFactory s, ILogger<RevivalHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryReadReviveType(payload.Span, out var reviveType))
        {
            Logger.LogWarning("Session {Id}: malformed ReqPCRevival ({Len} bytes)", session.SessionId, payload.Length);
            return;
        }

        // The duplicate the client fires right after a revival arrives here with the map still loading (the
        // type-0 revival's WarpAsync cleared MapLoaded before its 2311). It is dropped, since handling it would
        // start a second warp while the client loads the first.
        if (!player.MapLoaded || player.PendingGliderDismount)
        {
            Logger.LogDebug("{Name} sent a 1372 (type {Type}) while loading a map, ignored", player.Name, reviveType);
            return;
        }

        // Type 2 asks for the WoE / Kafra-seasoning save location, which this server has none of: the
        // "location unavailable" 2399 (24333), dead or not, and nothing else changes (class remarks).
        if (reviveType == ReviveAtSeasoningLocation)
        {
            await SendResultAsync(session, player, reviveType, SeasoningLocationUnavailable);
            return;
        }

        // Type 1 and any other value: no reply and no established menu option (class remarks).
        // A dead PC keeps its death window and can still choose the save point.
        if (reviveType != ReviveAtSavePoint)
        {
            Logger.LogDebug("{Name}: 1372 type {Type} has no captured answer, ignored (dead={Dead})",
                player.Name, reviveType, player.IsDead || player.Hp <= 0);
            return;
        }

        // Same dead test as EffectService and RegenService: NpcCombatService writes HP 0 and sets IsDead
        // together on the lethal hit. A live PC gets a result-code 2399 and keeps its HP, SP and map.
        if (!player.IsDead && player.Hp > 0)
        {
            await SendResultAsync(session, player, reviveType, NotDeadResultType0);
            return;
        }

        Logger.LogInformation("Session {Id}: {Name} reviving (type {Type})", session.SessionId, player.Name, reviveType);

        // Half of both bars, floored (class remarks). The HP floor only keeps a revived PC out of the Hp <= 0
        // dead test; every max this server computes is far above 2.
        RestoreVitals(player, Math.Max(MinRevivedHp, player.MaxHp / ReviveVitalsDivisor), player.MaxMp / ReviveVitalsDivisor);

        var data = services.GetRequiredService<GameDataStore>();
        var (mapId, x, y, z, source) = TeleportSavePointHandler.ResolveSavePoint(player, data);

        var body = BuildOnRevivalNoPosition2399(player.EntityId);
        await session.SendRmiAsync(ZoneS2COpcodes.OnRevival, writer => writer.WriteBytes(body));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnRevival, writer => writer.WriteBytes(body));
        await SendNormalStateAsync(session, player);
        // A revival counts for the "Resurrected number" Kharas (Complete_Type 27), right after the 2399 / 2398.
        await KharaService.OnRevivedAsync(session, player, services, Logger, ct);
        await SendVitalsAsync(player);

        Logger.LogDebug("{Name} revives at save point: map {Map} ({X}, {Y}, {Z}) from {Source}",
            player.Name, mapId, x, y, z, source);
        if (!await WarpService.WarpAsync(session, player, mapId, x, y, z, services))
            Logger.LogWarning("{Name}: revival map {Map} is not in Map_List; revived in place", player.Name, mapId);
    }

    /// <summary>Sets the revived PC's live vitals and clears the dead state.</summary>
    /// <remarks>
    /// Only PlayerState changes. The half-HP revival survives the map load it starts because
    /// AnsMapLoadingHandler now reads the live PlayerState.Hp/Mp both for its "was at full" test and for the
    /// current HP/SP of its 2498 (the BuildFullPcTrait2498 overload that takes them). An earlier version also
    /// copied HP/SP into the world-entry <see cref="PlayerState.Character"/> snapshot to get through that map
    /// load; the copy then stayed at the revival half for the rest of the session and every 2498 still built
    /// from the snapshot repainted the bars to it, so it is gone.
    /// It also drops the revival-skill offers the corpse was sent (3016, see RevivalSkillHandler): once the
    /// PC is back none of them is honoured, and a leftover offer would otherwise still
    /// match a 1684 after the PC's next death.
    /// </remarks>
    private static void RestoreVitals(PlayerState player, int hp, int sp)
    {
        player.ReviveTo(hp, sp);
        player.ClearRevivalOffers();
    }

    /// <summary>
    /// 2401 OnActorAutoHeal with the revived bars, to the reviver and everyone on its map
    /// (<see cref="ActorVitalsBroadcast"/>), as RegenService sends it. It is sent right after the revival's
    /// 2398, so onlookers who watched the PC die see it come back with
    /// bars. 2399 and 2398 carry no vitals, so without it the bars stay at the 0 of the lethal hit until
    /// something else reports them. It goes out on the death map, before the warp to the save point (the warp's
    /// 2311 follows, unchanged), so the viewers it reaches are the ones the 2399 just told.
    /// </summary>
    private Task SendVitalsAsync(PlayerState player) => ActorVitalsBroadcast.SendAutoHealAsync(Broadcast, player);

    /// <summary>
    /// Refuses a 1372 with a result-code 2399, to the requester only (class remarks): code 1 for a
    /// type-0 request from a PC that is not dead, 24333 for any type-2 request. Nothing else changes: no
    /// heal, no 2398, no warp. No onlooker is told.
    /// </summary>
    private async Task SendResultAsync(ProudSession session, PlayerState player, byte reviveType, ushort resultCode)
    {
        Logger.LogDebug("{Name}: 1372 type {Type} refused with result {Code} (dead={Dead})",
            player.Name, reviveType, resultCode, player.IsDead || player.Hp <= 0);
        var body = BuildOnRevivalResult2399(player.EntityId, resultCode);
        await session.SendRmiAsync(ZoneS2COpcodes.OnRevival, writer => writer.WriteBytes(body));
    }

    /// <summary>
    /// 2398 OnChangeState taking the revived PC out of the dead state 6 that NpcCombatService broadcast on the
    /// lethal hit (<see cref="PlayerDeathPackets.BuildOnChangeStateDead2398"/>). Without it a revival with no
    /// map reload (the not-in-Map_List fallback) leaves the client in state 6, and onlookers see a corpse.
    /// </summary>
    /// <remarks>
    /// Sent right after every 2399, as the very next S2C frame, for the same handle, identical to
    /// <c>BuildChangeState2398(handle, 0, 0, 2)</c>. It goes to the reviver and to
    /// the map, like the 2399; BroadcastToNearbyAsync skips the sender, who gets its own copy first. It is not
    /// sent after a refusal 2399 (blob+12 non-zero). blob+19 = 2 with blob+15 = 0 is the field-map form;
    /// the PK-map death form (blob+15 = 1, blob+19 in 1001..1008) is not reproduced by
    /// <see cref="PlayerDeathPackets.BuildOnChangeStateDead2398"/> either, so the revival matches the death this
    /// server sent. blob+19 is a fixed value, not modelled.
    /// </remarks>
    private async Task SendNormalStateAsync(ProudSession session, PlayerState player)
    {
        var normal = CombatPackets.BuildChangeState2398(player.EntityId, CombatPackets.ActorState.PcNormal, NoTargetHandle, RevivedStateTrailer);
        await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, normal);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(normal));
    }

    /// <summary>Reads the u8 revive type at blob+8, tolerating trailing transport bytes.</summary>
    private static bool TryReadReviveType(ReadOnlySpan<byte> body, out byte reviveType)
    {
        reviveType = 0;
        // Lenient, so the trailing transport bytes do not fail the declared-length comparison.
        if (!body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob)) return false;
        if (blob.Length < RequestBlobLength || BinaryPrimitives.ReadUInt16LittleEndian(blob) < RequestBlobLength) return false;
        reviveType = blob[ReviveTypeBlobOffset];
        return true;
    }

    /// <summary>
    /// 2399 OnRevival for a type-0 revival: body 29,
    /// <c>[01 1b][u16 selfLen=27][u16 58089][u32 handle][u32 handle][u8 0][u8 0][u8 0][f32 0][f32 0][f32 0]</c>,
    /// (flag 0 and zero position). The position the PC ends up at comes in
    /// the 2311 that follows, not here. It is the result-code form with result 0.
    /// </summary>
    internal static byte[] BuildOnRevivalNoPosition2399(uint actorHandle) => BuildOnRevivalResult2399(actorHandle, ResultRevived);

    /// <summary>
    /// 2399 OnRevival with no position and <paramref name="resultCode"/> in the u16 at blob+12, which the
    /// client reads as a result code: 0 revives, non-zero shows an error.
    /// <c>[01 1b][u16 selfLen=27][u16 58089][u32 handle][u32 handle][u16 result][u8 0][f32 0][f32 0][f32 0]</c>.
    /// Used, for example, to answer a PC that was not dead (type 0 and type 2 requests).
    /// </summary>
    internal static byte[] BuildOnRevivalResult2399(uint actorHandle, ushort resultCode)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnRevival);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32(actorHandle);
            p.WriteUInt16(resultCode);   // blob+12
            p.WriteByte(NoPositionFlag); // blob+14: no position
            p.WriteSingle(NoCoordinate); // x
            p.WriteSingle(NoCoordinate); // y
            p.WriteSingle(NoCoordinate); // z
        });
        return pw.ToArray();
    }
}
