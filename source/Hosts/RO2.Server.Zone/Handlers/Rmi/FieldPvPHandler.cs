using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1517 ReqFieldPvP — challenge another player to a field duel.
/// </summary>
/// <remarks>
/// <para>
/// Body (14 bytes): <c>[compact prefix][u16 inner=12][u16 msgTag=58451][u32 0]
/// [u32 targetHandle]</c>, the target at body+10 (blob+8), 0x11-tagged.
/// </para>
/// <para>
/// The challenge is answered with 2735 AnsCertifyFieldPvP to BOTH players: the challenger's copy follows the
/// request closely, and the target's copy is the invitation its 1518 answers
/// (the target's 2735 names the other player as challenger and itself as
/// target). Each copy carries the recipient's own handle first: <c>[self][challenger][target][duelId]</c>.
/// </para>
/// <para>
/// Refusals are 2742 AnsReqErrorFiledPvP <c>[self][u16 code]</c> to the challenger (see
/// <see cref="FieldPvPRejectPackets"/> for the layout and what each code shows), for the cases this server
/// can tell apart: the challenger dead (19) or the target dead (20), then the target already in an
/// accepted duel (12) or holding a pending challenge (13), then the two standing farther apart than
/// <see cref="FieldPvPService.DuelRange"/> (21, "Not close enough to battle."). The rest stay silent, as
/// before: a target that is not a player on the challenger's map (codes 15/16 share one string) and a challenger that is itself in a duel (no code
/// is defined for it). Some requests draw no 2735 either.
/// See <see cref="FieldPvPService"/> for what the duel does and does not cover.
/// </para>
/// </remarks>
public class FieldPvPHandler : ZoneRmiHandlerBase<FieldPvPHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFieldPvP;
    public override string Name => "FieldPvP";

    /// <summary>Offset of the target handle in the 1517 body (blob+8 after the 2-byte compact prefix).</summary>
    internal const int TargetOffset = 10;

    /// <summary>Size of the target handle (u32) in the 1517 body.</summary>
    private const int TargetHandleSize = sizeof(uint);

    public FieldPvPHandler(IServiceScopeFactory s, ILogger<FieldPvPHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < TargetOffset + TargetHandleSize) return;
        var targetHandle = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(TargetOffset));

        // A PC handle is (sessionId << 8) | 0x11 (PlayerStateManager.BuildActorHandle).
        var target = (targetHandle & 0xFF) == PlayerStateManager.PcActorTag
            ? PlayerManager.GetPlayer(targetHandle >> 8)
            : null;
        if (target is null || target.EntityId != targetHandle || target.MapId != player.MapId
            || target.SessionId == player.SessionId)
        {
            Logger.LogInformation("{Name} challenged {Target:X8}, which is not another player on map {Map}; no reply",
                player.Name, targetHandle, player.MapId);
            return;
        }

        if (RefusalCode(player, target, PlayerManager) is { } code)
        {
            Logger.LogInformation("{Name} challenged {Other}: refused with 2742 code {Code}", player.Name, target.Name, code);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsReqErrorFiledPvP,
                FieldPvPRejectPackets.BuildReject2742(player.EntityId, code), ct);
            return;
        }

        var duel = FieldPvPService.Open(player, target, PlayerManager);
        if (duel is null)
        {
            // The challenger is in a duel itself, or the target entered one between the check above and here.
            Logger.LogInformation("{Name} challenged {Other}, but one of them is already in a duel; no reply",
                player.Name, target.Name);
            return;
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsCertifyFieldPvP,
            FieldPvPPackets.BuildCertify2735(player.EntityId, duel.Challenger, duel.Target, duel.Id), ct);

        var targetSession = Broadcast.GetSession(target.SessionId);
        if (targetSession is { IsConnected: true })
            await targetSession.SendInternalRmiAsync(ZoneS2COpcodes.AnsCertifyFieldPvP,
                FieldPvPPackets.BuildCertify2735(target.EntityId, duel.Challenger, duel.Target, duel.Id), ct);

        Logger.LogInformation("{Name} challenged {Other} to duel {Id}", player.Name, target.Name, duel.Id);
    }

    /// <summary>
    /// The 2742 code a challenge from <paramref name="challenger"/> to <paramref name="target"/> is refused
    /// with, or null when it is not refused with one (it goes on to <see cref="FieldPvPService.Open"/>,
    /// which still returns null, silently, for a challenger already in a duel).
    /// </summary>
    internal static ushort? RefusalCode(PlayerState challenger, PlayerState target, PlayerStateManager players)
    {
        if (challenger.IsDead) return FieldPvPRejectPackets.ChallengerDead;
        if (FieldPvPService.LiveDuelState(challenger.EntityId, players) is not null) return null;
        if (target.IsDead) return FieldPvPRejectPackets.TargetDead;
        var busy = FieldPvPService.LiveDuelState(target.EntityId, players) switch
        {
            FieldPvPService.DuelState.Accepted => FieldPvPRejectPackets.TargetInBattle,
            FieldPvPService.DuelState.Pending => FieldPvPRejectPackets.TargetOnBattleCue,
            _ => (ushort?)null,
        };
        if (busy is not null) return busy;
        // Range last: it is the only check that reads positions, the ones the clients last reported
        // (PlayerState.X/Y, kept by ViewGroupMoveHandler), which is what FieldPvPService.DuelRange is measured against.
        return FieldPvPService.WithinDuelRange(challenger, target) ? null : FieldPvPRejectPackets.TooFar;
    }
}
