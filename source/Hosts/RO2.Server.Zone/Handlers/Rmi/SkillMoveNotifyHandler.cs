using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Combats.Domain;
using RO2.Combats.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1523 ReqSkillMoveNotify (raw 25 bytes): a ground-object skill has dragged an actor (the cast's
/// target) one step to the effect's position.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout</b> (NONE family, read_side Blob&lt;25&gt;):
/// <c>[u32 movedHandle][u32 skillId][u8 moveKind][f32 x][f32 y][f32 z][f32 extra]</c>.
/// moveKind is a field of the skill's data row: the same skill-data kind the skill execution switches on
/// (9, 14..22), and the one the 2745 and 2749 consumers switch on. The u8 at +24 is the high byte of the
/// +21 float, not a separate field.
/// </para>
/// <para>
/// <b>The sender</b> is the client's ground-object effect update. It sends the cast's
/// TARGET handle as movedHandle, the constant 0.0 as the extra float, and the ground object's current
/// position as x/y/z. That same position is also applied to the target actor on the client just before the
/// send. So the request must not be parsed as skill at +0 with floats at +5/+9/+13 written to the caster,
/// which would read the handle as a skill and move the wrong actor.
/// </para>
/// <para>
/// <b>2745 NfySkillMoveNotify stays unsent.</b> It would carry
/// <c>[hdr 8][u32 casterHandle @8][this request's 25 bytes verbatim @12..36]</c>: blob+12 movedHandle, +16
/// skill, +20 kind, +21..32 xyz, +33 f32. But its msgTag is not defined, and it has its
/// own roadmap row.
/// </para>
/// </remarks>
public class SkillMoveNotifyHandler : ZoneRmiHandlerBase<SkillMoveNotifyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillMoveNotify;
    public override string Name => "SkillMoveNotify";

    /// <summary>4 + 4 + 1 + 12 + 4: the builder's fixed struct.</summary>
    internal const int BodyLength = 25;

    public SkillMoveNotifyHandler(IServiceScopeFactory s, ILogger<SkillMoveNotifyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>The fields of a 1523 body; false when it is shorter than the builder's 25 bytes.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out uint movedHandle, out uint skillId, out byte moveKind,
        out float x, out float y, out float z)
    {
        movedHandle = skillId = 0; moveKind = 0; x = y = z = 0f;
        if (body.Length < BodyLength) return false;
        movedHandle = BinaryPrimitives.ReadUInt32LittleEndian(body);
        skillId = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        moveKind = body[8];
        x = BinaryPrimitives.ReadSingleLittleEndian(body[9..]);
        y = BinaryPrimitives.ReadSingleLittleEndian(body[13..]);
        z = BinaryPrimitives.ReadSingleLittleEndian(body[17..]);
        // +21: the sender's constant 0.0, not read.
        return true;
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var movedHandle, out var skillId, out var moveKind, out var x, out var y, out var z))
        {
            Logger.LogWarning("{Name}: ReqSkillMoveNotify of {Len} bytes, expected {Expected}", player.Name, payload.Length, BodyLength);
            return Task.CompletedTask;
        }
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            Logger.LogWarning("{Name}: ReqSkillMoveNotify for skill {SkillId} with a non-finite position; ignored", player.Name, skillId);
            return Task.CompletedTask;
        }
        player.LastActivity = DateTime.UtcNow;

        _ = ApplyToMovedActor(player, services.GetRequiredService<NpcStateManager>(), skillId, moveKind, movedHandle, x, y, z, Logger, "move",
            services.GetRequiredService<MovementGuard>());

        // 1525 carries no handle, so the moved actor is remembered for it (SkillMoveEndHandler), refused or not:
        // the 1525 then puts its end point through the same checks instead of mistaking a refused NPC drag for
        // the caster's own move and teleporting the caster to the monster's end point.
        SkillMoveTracker.Record(player, skillId, new SkillMoveLedger.SkillMove(movedHandle, moveKind, x, y, z));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Puts the actor <paramref name="movedHandle"/> names at the reported point and says whether it did: the
    /// caster itself (player X/Y/Z), or a monster the caster's latest cast is acting on (PosX/PosY; NPCs keep no
    /// Z here). Anything else is only logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caster may move itself only through <see cref="SelfMoveRefusal"/> (an accepted cast of this skill, a
    /// self-move, within the skill's reach). Moving an NPC writes server state that
    /// range checks, aggro and the 2319 newcomers see, so a 1523/1525 may move one only when ALL of these hold:
    /// </para>
    /// <list type="number">
    /// <item>it is the +16 target of the caster's latest accepted 1508 of exactly <paramref name="skillId"/>, on
    /// this map (<see cref="SkillMoveTracker.IsCastTarget"/>, recorded by SkillStartHandler past its cooldown refusal). That is the only handle the client
    /// can put at 1523 +0: it is the target the client then sends at 1508 +16 (SkillMoveTracker remarks);</item>
    /// <item>the claimed move kind is one under which the cast stores that target at all
    /// (<see cref="SkillMoveTracker.IsTargetMoveKind"/>: 9, 14, 17, 20, 22);</item>
    /// <item>it is a live monster (Camp 2+, the camp SkillTickTargets and the threat table require; friendly NPCs are no skill target) on the caster's map;</item>
    /// <item>both where the server holds it now and the reported point lie within the skill's reach of the caster
    /// (<see cref="SkillMoveTracker.ReachFor"/>: Skill_Range + Influence_Bound_Length + the client's 4-unit
    /// target slack), in 2D because NPCs keep no Z.</item>
    /// </list>
    /// <para>
    /// A refused move changes nothing here: the NPC keeps the position the server already gave it. No answer
    /// exists to send: 2745 stays unsent.
    /// </para>
    /// </remarks>
    internal static bool ApplyToMovedActor(PlayerState caster, NpcStateManager npcs, uint skillId, byte moveKind,
        uint movedHandle, float x, float y, float z, ILogger logger, string step, MovementGuard guard)
    {
        if (movedHandle == caster.EntityId)
        {
            // A self-move is a server-granted skill move (dash / skill move) and legitimately exceeds the walk
            // cap, so it goes through the exemption (step-length check skipped, finiteness and bounds still apply, the
            // move baseline refreshed). But it is only taken for a skill the caster has an accepted cast of, and its
            // end point must lie within the skill's reach of where the caster stands: otherwise any client could
            // teleport by sending a 1523/1525 naming itself. A refused one is log-and-ignore.
            var selfRefusal = SelfMoveRefusal(caster, skillId, moveKind, x, y);
            if (selfRefusal is not null)
            {
                logger.LogDebug("{Name}: skill {SkillId} {Step} moves the caster itself on map {Map}; position not taken: {Reason}",
                    caster.Name, skillId, step, caster.MapId, selfRefusal);
                return false;
            }
            return guard.ApplyServerGranted(caster, new MovePos(x, y, z)) == MoveRefusal.None;
        }

        var refusal = NpcMoveRefusal(caster, npcs, skillId, moveKind, movedHandle, x, y, out var npc);
        if (refusal is not null)
        {
            logger.LogDebug("{Name}: skill {SkillId} {Step} names actor 0x{Handle:X8} on map {Map}; position not taken: {Reason}",
                caster.Name, skillId, step, movedHandle, caster.MapId, refusal);
            return false;
        }

        npc!.PosX = x;
        npc.PosY = y;
        // The NPC no longer stands on the walk its last 2318 described, so a 2319 must not
        // interpolate along it (NpcSpawnService.BuildOnNpcViewData2319 reads LastMove).
        npc.LastMove = null;
        return true;
    }

    /// <summary>Why a 1523/1525 may not move the caster itself, or null when it may (see
    /// <see cref="ApplyToMovedActor"/>).</summary>
    /// <remarks>
    /// Taken only when ALL hold: the caster has an accepted cast of exactly <paramref name="skillId"/> on this map
    /// (<see cref="SkillMoveTracker.IsCastSkill"/>); the move is a self-move, i.e. the claimed kind is not one that
    /// stores a foreign cast target (the complement of <see cref="SkillMoveTracker.IsTargetMoveKind"/>) OR the
    /// accepted cast's target was the caster itself (a target-storing kind whose +16 target is the caster is the
    /// only way the client can name the caster's own handle at 1523 +0, so refusing it would
    /// refuse every legitimate self-move); the skill group has a Skill_Table_Base row; and the end point is within
    /// <see cref="SkillMoveTracker.ReachFor"/> of the caster's current X/Y (2D).
    /// </remarks>
    internal static string? SelfMoveRefusal(PlayerState caster, uint skillId, byte moveKind, float x, float y)
    {
        // How far a self-move may carry its caster is not defined by data; the skill's own Skill_Range +
        // Influence_Bound_Length is the conservative bound.
        var reach = SkillMoveTracker.ReachFor(skillId / 100u);
        var refusal = SkillMoveAuthorization.SelfMove(
            hasCastOfSkill: SkillMoveTracker.IsCastSkill(caster, caster.MapId, skillId),
            moveKindStoresTarget: SkillMoveTracker.IsTargetMoveKind(moveKind),
            castTargetedSelf: SkillMoveTracker.IsCastTarget(caster, caster.MapId, skillId, caster.EntityId),
            reach, caster.X, caster.Y, x, y);
        return refusal switch
        {
            SkillMoveAuthorization.MoveRefusal.None => null,
            SkillMoveAuthorization.MoveRefusal.NoAcceptedCast => "caster has no accepted cast of this skill on this map",
            SkillMoveAuthorization.MoveRefusal.SelfNotCastTarget => $"move kind {moveKind} stores a cast target and the caster's cast did not target itself",
            SkillMoveAuthorization.MoveRefusal.NoReachRow => "skill group has no Skill_Table_Base row",
            _ => $"end point is farther than {reach} from the caster",
        };
    }

    /// <summary>Why a 1523/1525 may not move NPC <paramref name="handle"/>, or null when it may (see
    /// <see cref="ApplyToMovedActor"/>).</summary>
    internal static string? NpcMoveRefusal(PlayerState caster, NpcStateManager npcs, uint skillId, byte moveKind,
        uint handle, float x, float y, out NpcRuntimeState? npc)
    {
        npc = null;
        var found = npcs.IsNpcEntity(handle) ? npcs.GetNpc(handle) : null;
        var moved = found is { } f
            ? new SkillMoveAuthorization.MovedNpc(true, f.IsDead, f.MapId == caster.MapId, f.Camp, f.PosX, f.PosY)
            : default;
        var reach = SkillMoveTracker.ReachFor(skillId / 100u);
        var refusal = SkillMoveAuthorization.NpcMove(moved,
            isCastTarget: SkillMoveTracker.IsCastTarget(caster, caster.MapId, skillId, handle),
            moveKindStoresTarget: SkillMoveTracker.IsTargetMoveKind(moveKind),
            reach, caster.X, caster.Y, x, y);
        if (refusal == SkillMoveAuthorization.MoveRefusal.None)
        {
            npc = found;
            return null;
        }
        return refusal switch
        {
            SkillMoveAuthorization.MoveRefusal.NotTrackedNpc => "neither the caster nor a tracked NPC",
            SkillMoveAuthorization.MoveRefusal.NotLiveMonster => "not a live monster of the caster's map",
            SkillMoveAuthorization.MoveRefusal.NotCastTarget => "not the target of the caster's latest accepted cast of this skill",
            SkillMoveAuthorization.MoveRefusal.MoveKindStoresNoTarget => $"move kind {moveKind} never stores a cast target",
            SkillMoveAuthorization.MoveRefusal.NoReachRow => "skill group has no Skill_Table_Base row",
            SkillMoveAuthorization.MoveRefusal.MonsterOutOfReach => $"monster stands farther than {reach} from the caster",
            _ => $"reported point is farther than {reach} from the caster",
        };
    }
}
