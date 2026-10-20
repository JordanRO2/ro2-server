using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Ends a function-object control (RMI 1412, ReqFunctionObjectControlReward) and applies the object's action.
/// </summary>
/// <remarks>
/// Wire layout: <c>[01 0c][u16 innerSize=4][u16 msgTag=58359][u32 pcHandle][u32 targetHandle]</c>.
/// The parse uses fixed offsets rather than innerSize. The target carries the function-object tag 0x14.
/// <para>
/// Portals and dungeon entrances are answered with the end message (2521) and the move to their destination.
/// Other object kinds pay their gathering reward, then send the state and end messages.
/// A reward for a control that is not active is ignored. One that arrives before the object's casting time
/// leaves the control running.
/// </para>
/// </remarks>
public class NpcRewardHandler : ZoneRmiHandlerBase<NpcRewardHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFunctionObjectControlReward;
    public override string Name => "NpcReward";

    /// <summary>Request body offset 10: u32 handle of the target object (the blob's second handle).</summary>
    private const int TargetHandleOffset = 10;

    /// <summary>Request body length: 14 bytes, ending with the target handle.</summary>
    private const int RequestLength = TargetHandleOffset + sizeof(uint);

    /// <summary>msgTag of the 2521 OnFuncObjectError message (58362, 0xE3FA).</summary>
    private const ushort TagFuncObjectEnd = 58362;

    /// <summary>System message id 0: nothing to report. A non-zero id marks a refused end (22208 = bag full).</summary>
    private const uint NoSystemMessage = 0;

    /// <summary>Template id 0: the target has no function-object template (a portal, an NPC or an unknown handle).</summary>
    private const uint NoTemplate = 0;

    /// <summary>
    /// Fraction of an object's Casting that must have run since its control began before a 1412 ends it. Half, not the
    /// whole: the client's bar clock can run ahead of wall time, so a genuine 1412 may arrive slightly early.
    /// </summary>
    private const double MinCastingFraction = 0.5;

    /// <summary>The u32 that precedes the target handle in the 2521 body; always 0.</summary>
    private const uint FuncObjectEndLeadField = 0;

    public NpcRewardHandler(IServiceScopeFactory s, ILogger<NpcRewardHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(TargetHandleOffset);
        var targetHandle = reader.ReadUInt32();

        if (!player.MapLoaded || player.PendingGliderDismount)
        {
            Logger.LogDebug("Session {Id}: {Name} sent a 1412 on {Target:X8} before acking its map load, not answered",
                session.SessionId, player.Name, targetHandle);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();
        uint templateId = NoTemplate;
        ushort castingMs = 0;
        var action = new FuncObjectAction(FuncObjectActionKind.None, 0);
        // The pose branch is decided by the control the 1410 began: the template's pose for a function object,
        // NpcControlState (0x14) for an NPC (0x13) target, null for a Type 8 object or a handle this server did not
        // spawn. Deciding it from TryGetTemplate alone (which accepts only 0x14 handles) would send an NPC 1412 down
        // the Type 8 branch: a bare 2521 with no state-0 reset and no 2519, leaving the 0x14 pose on the controller
        // and its onlookers.
        var controlState = FuncObjectControlPackets.ControlStateFor(data, player.MapId, targetHandle);
        if (FuncObjectControlPackets.TryGetTemplate(data, player.MapId, targetHandle, out var info))
        {
            templateId = info.ID;
            castingMs = info.Casting;
            action = FuncObjectActions.Resolve(data, info.ID);
        }

        if (controlState is not null)
        {
            // A kind with a pose (Types 1, 2, 4, 5, and an NPC, whose Casting is 0 here) is answered only as the end
            // of the control its 1410 began, once half the object's Casting has run since then: the control is what
            // stops a repeat. Anything else gets nothing. A too-early one leaves the control running, so the 1411 or
            // 1410 the client sends next still finds it.
            var ended = FuncObjectControlState.TryEndAfter(player, targetHandle, TimeSpan.FromMilliseconds(castingMs * MinCastingFraction),
                Stopwatch.GetTimestamp(), out var control, out var elapsed);
            if (ended != FuncObjectControlEnd.Ended || control.State is null)
            {
                Logger.LogWarning("Session {Id}: {Name} sent a 1412 on {Target:X8} ({Template}, Casting {Casting} ms) with no control of it that has run half its Casting ({End}, {Elapsed} ms since its 1410, pose {State}): not answered",
                    session.SessionId, player.Name, targetHandle, templateId, castingMs, ended,
                    (long)elapsed.TotalMilliseconds, control.State);
                return;
            }
        }
        else
        {
            // A portal or dungeon entrance (Type 8), or a target with no pose-kind template here, needs no control.
            // Whatever control on it did run ends here, so a later 1411 on it sends no reset.
            FuncObjectControlState.TryEnd(player, targetHandle, out _);
        }

        // A portal is checked before anything goes out, as WarpHandler does: a 2938/2505 with no move would despawn
        // the player from their own client for nothing.
        PositionData? destination = null;
        if (action.Kind == FuncObjectActionKind.Portal)
        {
            if (data.Positions.TryGetValue(action.Value, out var pos) && data.GetMap(pos.Area_ID) is not null)
                destination = pos;
            else
                Logger.LogWarning("Portal {Target:X8} ({Template}) names Position_Info {Pos}, which is missing or off Map_List: not moved",
                    targetHandle, templateId, action.Value);
        }

        uint systemMessageId = NoSystemMessage;
        if (controlState is not null)
        {
            // The kinds with a pose: the state-0 reset (to the player and the players around it, as for the cancel),
            // the payout, then 2519 ahead of the 2521. An NPC target takes the same order with no payout (it has no
            // FuncReward rows, so its template id is NoTemplate): reset, 2519 code 0, 2521 code 0. Both 2519 and 2521
            // clear the client's current-control slot.
            var reset = FuncObjectControlPackets.BuildControlReset2398(player.EntityId, targetHandle);
            await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(reset));
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(reset));

            if (templateId != NoTemplate)
                systemMessageId = await FuncObjectActionService.PayOutAsync(session, player, templateId, services, Logger,
                    Random.Shared, ct);
            // A completed harvest counts for the "Complete number of harvest" Kharas (Complete_Type 29), after the
            // payout and before the 2519. A payout refused for a full bag (a non-zero system message) is not counted,
            // nor is the end of an NPC control (no harvest: NoTemplate).
            if (systemMessageId == NoSystemMessage && templateId != NoTemplate)
                await KharaService.OnHarvestAsync(session, player, services, Logger, ct);
            await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObjectRewardResult,
                writer => writer.WriteBytes(FuncObjectRewardPackets.BuildRewardResult2519(targetHandle, systemMessageId)));
        }

        var end = BuildFuncObjectEnd2521(targetHandle, systemMessageId);
        await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObjectError, writer => writer.WriteBytes(end));
        Logger.LogDebug("Session {Id}: {Name} ended control of {Target:X8} ({Template}, action {Action} {Value}, code {Code})",
            session.SessionId, player.Name, targetHandle, templateId, action.Kind, action.Value, systemMessageId);
        if (systemMessageId != NoSystemMessage) return;   // a refused end (bag full) does nothing further

        switch (action.Kind)
        {
            case FuncObjectActionKind.Portal when destination is not null:
                // WarpHandler's gate move: the departure (Warp_Handling 0 effects, the ride, an open Illusion Pyramid,
                // 2938 [01 00], the self 2505), then the same-connection move to the Position_Info row.
                await WarpDeparture.DepartAsync(session, player, services, Logger, ct);
                var ok = await WarpService.WarpAsync(session, player, destination.Area_ID,
                    destination.Position_X, destination.Position_Y, destination.Position_Z, services);
                Logger.LogInformation("Player {Name} took portal {Target:X8} ({Template}) to Position_Info {Pos}, map {Map} ({X}, {Y}, {Z}): warped={Ok}",
                    player.Name, targetHandle, templateId, action.Value, destination.Area_ID,
                    destination.Position_X, destination.Position_Y, destination.Position_Z, ok);
                break;

            case FuncObjectActionKind.DungeonEntrance:
                // 2741 right after the 2521: the client opens the level window of the group, whose OK sends the 1521.
                await session.SendInternalRmiAsync(ZoneS2COpcodes.ReqDungeonLevelSelect,
                    FuncObjectRewardPackets.BuildDungeonLevelSelect2741(action.Value));
                break;
        }
    }

    /// <summary>
    /// Builds the 2521 OnFuncObjectError message that ends a function-object control.
    /// </summary>
    /// <remarks>
    /// Layout: <c>[01 10][u16 16][u16 58362][u32 0][u32 targetHandle][u32 systemMessageId]</c>.
    /// systemMessageId is <see cref="NoSystemMessage"/> except for a refused end, which carries the system message that
    /// explains the refusal (22208 when the bag is full).
    /// </remarks>
    internal static byte[] BuildFuncObjectEnd2521(uint targetHandle, uint systemMessageId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagFuncObjectEnd);
            p.WriteUInt32(FuncObjectEndLeadField);
            p.WriteUInt32(targetHandle);
            p.WriteUInt32(systemMessageId);
        });
        return pw.ToArray();
    }
}
