using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1498 ReqNormalAttackStart - Start auto-attack</summary>
public class NormalAttackStartHandler : ZoneRmiHandlerBase<NormalAttackStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqNormalAttackStart;
    public override string Name => "NormalAttackStart";

    /// <summary>
    /// 2695 code for a 1498 whose target is not an actor handle at all (its low byte is none of the 0x11/0x13/0x14
    /// tags, <see cref="AutoAttackState.IsActorHandle"/>). Kept here rather than in the shared CombatPackets, next to
    /// the only code that sends it.
    /// </summary>
    internal const ushort AttackRefusedNotAnActor = 20014;

    /// <summary>1498 body: a single u32 at @0, the target's actor handle (no envelope in front of it).</summary>
    private const int RequestBodyLength = sizeof(uint);

    /// <summary>Mask of an actor handle's low byte, its actor type tag (0x11 PC, 0x13 NPC, 0x14 function object).</summary>
    private const uint ActorTagMask = 0xFF;

    public NormalAttackStartHandler(IServiceScopeFactory s, ILogger<NormalAttackStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength) return;
        var reader = new PacketReader(payload.Span);

        // 1498 has no compact-length envelope: the body starts directly with the target's actor
        // handle (for example "13 D4 00 00" is handle 0x0000D413, whose low byte is the 0x13 NPC tag).
        var targetEntityId = reader.ReadUInt32();

        // Answer the request, and answer it honestly. The server never initiates a player's
        // swing; it only grants or refuses one. The cycle is client-clocked:
        //
        //   C2S 1498 -> S2C 2694 (grant) -> C2S 1499 (the client's animation reaches impact)
        //                                -> S2C 2696 (damage answer) -> next 1498
        //
        // The grant gates the rest of the cycle, but granting unconditionally is also wrong: a granted
        // swing has no way to end, so arriving at a target without attacking would leave the character
        // holding the attack animation, and interrupting a swing by walking away would make the
        // client cancel, re-send 1498, and get another grant for a swing it had just abandoned.
        // The refusal, 2695 AnsErrorNormalAttack, is what takes it back out of the attack state.
        if (!AutoAttackState.IsActorHandle(targetEntityId))
        {
            AutoAttackState.Disengage(player);
            Logger.LogInformation("{Name} REFUSED attack on 0x{Target:X8}: not an actor handle (20014)",
                player.Name, targetEntityId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsErrorNormalAttack,
                CombatPackets.BuildAttackRefused2695(AttackRefusedNotAnActor), ct);
            return;
        }

        var npcState = services.GetRequiredService<NpcStateManager>();
        var npc = npcState.GetNpc(targetEntityId);

        // A PC target is attackable only as the attacker's opponent in a started field duel (FieldPvPCombat),
        // and is granted like a monster.
        var opponent = npc is null ? FieldPvPCombat.TryGetStartedOpponent(player, targetEntityId, PlayerManager) : null;
        if (opponent is { } duelist)
        {
            await GrantAsync(player, targetEntityId, duelist.Victim.X, duelist.Victim.Y);
            services.GetService<PcCombatStateService>()?.MarkCombat(player);
            return;
        }

        // Range is not a precondition for the grant. 1498 is sent when the player presses attack,
        // before the client runs to the target, so the request routinely arrives from well outside
        // melee reach. Refusing it would kill the whole swing and make the avatar arrive at the
        // target, slide in place, and only then strike.
        //
        // The 4-unit reach describes where impacts land, not where a swing may be requested. It is
        // enforced at 1499, once the client has arrived, in NormalAttackDamageHandler.
        var refused =
            npc is null ? "not a known actor" :
            npc.IsDead ? "target is dead" :
            npc.Camp <= 1 ? "target is not hostile" :
            null;

        if (refused is not null)
        {
            AutoAttackState.Disengage(player);
            var dist = npc is null ? -1f
                : MathF.Sqrt((npc.PosX - player.X) * (npc.PosX - player.X)
                           + (npc.PosY - player.Y) * (npc.PosY - player.Y));
            Logger.LogInformation(
                "{Name} REFUSED attack on 0x{Target:X8}: {Why} (dist={Dist:F2}, reach={Reach}, "
                + "player=({PX:F1},{PY:F1}) npc=({NX:F1},{NY:F1}))",
                player.Name, targetEntityId, refused, dist, AutoAttackState.MaxAttackRange,
                player.X, player.Y, npc?.PosX ?? 0f, npc?.PosY ?? 0f);
            // The code is the only thing the client reads from 2695 (it picks the error toast).
            var code = RefusalCode(npc is null, npc is not null && npc.IsDead, targetEntityId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsErrorNormalAttack,
                CombatPackets.BuildAttackRefused2695(code));
            return;
        }

        await GrantAsync(player, targetEntityId, npc!.PosX, npc.PosY);
        services.GetService<PcCombatStateService>()?.MarkCombat(player);
    }

    /// <summary>Actor type tag (low byte) of a PC handle; see <see cref="ActorTagMask"/>.</summary>
    private const uint PcHandleTag = PlayerStateManager.PcActorTag;

    /// <summary>
    /// The 2695 code of a refused 1498 on an actor handle. A refused PC target (one that is not this player's started
    /// field-duel opponent, so no NpcState was found) takes 20122, which is not a death code for a PC; it is simply
    /// the code for a refused PC target. For an NPC, dead and alive targets are told apart: 20122 when the target
    /// died first, 22301 when it had not; see CombatPackets.AttackRefusedTargetDead. "Not a known actor" for a non-PC
    /// handle and "not hostile" are not deaths, so they keep the live-target code.
    /// </summary>
    internal static ushort RefusalCode(bool noNpcState, bool npcDead, uint targetEntityId) =>
        noNpcState && (targetEntityId & ActorTagMask) == PcHandleTag ? CombatPackets.AttackRefusedTargetDead
        : npcDead ? CombatPackets.AttackRefusedTargetDead
        : CombatPackets.AttackRefusedLiveTarget;

    /// <summary>
    /// Grants the swing: engages (and arms the impact, AutoAttackState.Engage) and sends the 2694 grant, which
    /// goes to the attacker himself only once he is in reach and settled (see below), and always to the other
    /// players on the map. <paramref name="targetX"/>/<paramref name="targetY"/> are the target's position: the
    /// monster's, or the duel opponent's last reported one.
    /// </summary>
    private async Task GrantAsync(PlayerState player, uint targetEntityId, float targetX, float targetY)
    {
        AutoAttackState.Engage(player, targetEntityId);
        {
            var d = MathF.Sqrt((targetX - player.X) * (targetX - player.X)
                             + (targetY - player.Y) * (targetY - player.Y));
            Logger.LogInformation("{Name} GRANTED attack on 0x{Target:X8} (dist={Dist:F2})",
                player.Name, targetEntityId, d);
        }

        // The 2694 grant goes to the attacker himself only when he is already in melee range and
        // settled, and always to the other players on the map.
        //
        // The client has no own-handle guard on 2694: it force-transitions whatever actor blob+12
        // names into attack-state 802. The attacker needs this to complete the swing, since it is what
        // drives him from 1498 to 1499; without it he arrives, stops, and never strikes. But delivered
        // while he is still auto-running to the target it overlays the run animation and he orbits the
        // target (skating).
        //
        // Players normally request an attack from a settled stop, so the self-2694 lands after arrival
        // and the sequence is 1498 -> 2694(self) -> 1499 all at the stop. We reproduce that by gating
        // the self copy on being in range and not moving: otherwise the attacker only gets the
        // observer broadcast and keeps approaching; the auto-attack loop re-requests once he arrives,
        // and that in-range 1498 delivers the self-2694 that fires the swing. The InRange radius (4)
        // can be satisfied while the avatar is still walking the last couple of units, so range alone
        // is not enough; the self-2694 waits until the client has stopped (its 1526 reports cur == dst).
        var inRange = !player.IsMoving && AutoAttackState.InRange(player, targetX, targetY);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyNormalAttackStart,
            writer => writer.WriteBytes(
                CombatPackets.BuildAttackStart2694(player.EntityId, targetEntityId)),
            excludeSessionId: inRange ? null : player.SessionId);
    }
}
