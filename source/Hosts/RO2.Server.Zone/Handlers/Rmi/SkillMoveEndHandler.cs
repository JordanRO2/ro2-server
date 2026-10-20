using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1525 ReqSkillMoveEnd (Fixed 21B) - End movement skill at final position</summary>
public class SkillMoveEndHandler : ZoneRmiHandlerBase<SkillMoveEndHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillMoveEnd;
    public override string Name => "SkillMoveEnd";

    /// <summary>
    /// Below this displacement the move has no direction to derive a yaw from. 0.01 units is the
    /// resolution NPC positions travel at (2318/2506 carry trunc(units*100)).
    /// </summary>
    private const float MinYawDisplacement = 0.01f;

    public SkillMoveEndHandler(IServiceScopeFactory s, ILogger<SkillMoveEndHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Fixed 21B:
        // [u32 skillId][u8 moveKind][f32 x][f32 y][f32 z][f32 @17] (4+1+12+4 = 21). x/y/z is the ground
        // effect's end point, sent when the effect has ended.
        //
        // +17 is not a facing. The client always writes 0.0 there, so it is not read.
        if (payload.Length < 21) return;
        var reader = new PacketReader(payload.Span);
        var skillId = reader.ReadUInt32();
        // +4: the skill's move kind (a field of its skill-data row), which the client's skill execution and the
        // 2749 consumer switch on. Forwarded at 2749 blob+16.
        var moveKind = reader.ReadByte();
        var finalX = reader.ReadSingle();
        var finalY = reader.ReadSingle();
        var finalZ = reader.ReadSingle();
        // +17: always 0.0, see above.
        if (!float.IsFinite(finalX) || !float.IsFinite(finalY) || !float.IsFinite(finalZ))
        {
            Logger.LogWarning("{Name}: ReqSkillMoveEnd for skill {SkillId} with a non-finite position; ignored", player.Name, skillId);
            return;
        }
        player.LastActivity = DateTime.UtcNow;

        // Which actor ends here. The end point is the ground effect's point, and the actor
        // that effect drags is the cast target, named at +0 of every 1523 before this request
        // (SkillMoveNotifyHandler, SkillMoveTracker). 1525 itself carries no handle. With an entry, the end
        // point goes to that actor. Without one (no 1523 seen), the caster is moved if SelfMoveRefusal allows it.
        // The previous position is captured before it is overwritten: the yaw model below needs the leg
        // that ends here.
        float prevX, prevY;
        var npcs = services.GetRequiredService<NpcStateManager>();
        var guard = services.GetRequiredService<MovementGuard>();
        if (SkillMoveTracker.TryTake(player, skillId, out var tracked) && tracked.MovedHandle != player.EntityId)
        {
            // The tracked position is where the last 1523 reported the target, i.e. that actor's last known
            // point on this move. The end point goes through the same checks a 1523 does (the target of the
            // caster's latest accepted cast of this skill, a target-storing move kind, a live monster of this
            // map, both points within the skill's reach of the caster); a refused one is only logged and the
            // caster is NOT moved in its place. NPCs hold no Z here.
            prevX = tracked.X;
            prevY = tracked.Y;
            _ = SkillMoveNotifyHandler.ApplyToMovedActor(player, npcs, skillId, tracked.MoveKind, tracked.MovedHandle,
                finalX, finalY, finalZ, Logger, "move end", guard);
        }
        else
        {
            // Self-targeted (the 1523s named the caster) or untracked: the last position this server holds
            // for the caster (1526 moves, 1523 skill-move notifies). A server-granted skill move (step-length cap
            // skipped, finiteness and bounds still apply, move baseline refreshed), but only for a skill the
            // caster has an accepted cast of, as a self-move, within the skill's reach (SkillMoveNotifyHandler
            // .SelfMoveRefusal). A refused one is log-and-ignore: the caster does not move and nothing is sent.
            prevX = player.X;
            prevY = player.Y;
            if (!SkillMoveNotifyHandler.ApplyToMovedActor(player, npcs, skillId, moveKind, player.EntityId,
                    finalX, finalY, finalZ, Logger, "move end", guard))
                return;
        }

        Logger.LogDebug("{Name} skill move end {SkillId} at ({X}, {Y}, {Z})",
            player.Name, skillId, finalX, finalY, finalZ);

        // AnsSkillMoveEnd (2748) ack. The client reads a single fixed Blob<6>, with the same field set as
        // 2744/2746: [u16 result][u32 skillId]. A longer body would over-run that read and put the
        // handle's low word where the result goes.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSkillMoveEnd, writer =>
        {
            writer.WriteUInt16(0);       // result = success
            writer.WriteUInt32(skillId); // echoed from the C2S 1525 request
        });

        // Broadcast NfySkillMoveEnd (2749). 2749 is compact-BA (msgTag 58465), so the body must carry the
        // compact envelope; a raw body [u32 sessionId][u32 skillId][f32 x/y/z] would crash the remote
        // client (first byte not a compact marker). Blob: [hdr 8][u32 casterHandle @8][u32 skillId @12]
        // [u8 moveKind @16][f32 x][f32 y][f32 z][f32 yaw @29] (ActorStatePackets.BuildNfySkillMoveEnd).
        //
        // blob+29 is a yaw in radians spanning -pi..+pi, stored by the client on the actor. The request gives
        // no yaw (+17 is the constant 0.0 above), so it is derived from the move, as a server-side
        // model: yaw = atan2(dx, -dy) over (final - last known position). This is the axis convention
        // used for NPC facing; it is assumed to hold for a PC's end-of-move facing as well.
        //
        // With no displacement there is no direction, and the server tracks no facing of its own:
        // player.Rotation is what 2504 +61 advertises for this actor, but nothing updates it after
        // login, so this fallback is 0 in practice, confined to a zero-length move.
        var dx = finalX - prevX;
        var dy = finalY - prevY;
        var yaw = dx * dx + dy * dy >= MinYawDisplacement * MinYawDisplacement
            ? MathF.Atan2(dx, -dy)
            : player.Rotation;
        if (!float.IsFinite(yaw)) yaw = 0f; // never forward a non-finite value into others' rotation code
        // blob+8 stays the CASTER: the client resolves the running skill from that actor's own skill map.
        // blob+12/+16 are the request's skill and move kind; with 0 there, every onlooker's lookup for a PC
        // would miss and the 2749 would do nothing.
        var body = ActorStatePackets.BuildNfySkillMoveEnd(player.EntityId, skillId, moveKind, finalX, finalY, finalZ, yaw);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySkillMoveEnd,
            writer => writer.WriteBytes(body));
    }
}
