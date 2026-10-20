using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1519 ReqCancelFiledPvP — cancel a field-duel challenge, or forfeit an accepted duel.
/// </summary>
/// <remarks>
/// <para>
/// Body (16 bytes): <c>[01 0e][u16 inner=14][u16 msgTag=58453][u32 0][u32 duelId][u16 code]</c>, the duel id
/// at body+<see cref="DuelIdOffset"/> and the code at body+<see cref="CodeOffset"/>. Codes: 3
/// (<see cref="CodeCancel"/>) and 5 (<see cref="CodeForfeit"/>).
/// </para>
/// <para>
/// Answers:
/// <list type="bullet">
/// <item>code 3 → 2738 NfyCancelFiledPvP <c>[self][duelId][u16 3][canceller]</c> to both players, the
/// canceller being the sender. The sender is usually the challenged player, but may be the challenger; the
/// challenger's client then closes its own window with a 1518 answer 3 (see <see cref="AcceptFieldPvPHandler"/>).
/// Then the end 2398 pair to the map (<see cref="FieldPvPStatePackets"/>).</item>
/// <item>code 5 → 2736 NfyResultFiledPvP <c>[self][winner][loser][u16 4]</c>, the sender as loser and
/// the other player as winner: a forfeit. Then the end 2398 pair to the map
/// (<see cref="FieldPvPStatePackets"/>).</item>
/// </list>
/// Retail also removes a duel effect around the cancel (2494 OnDelEffect, e.g. effect 0xF6B1); that
/// effect is never added by this server, so there is nothing to remove.
/// </para>
/// </remarks>
public class CancelFieldPvPHandler : ZoneRmiHandlerBase<CancelFieldPvPHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCancelFiledPvP;
    public override string Name => "CancelFieldPvP";

    /// <summary>Cancel code: withdraw a field-duel challenge (answered with 2738 NfyCancelFiledPvP).</summary>
    internal const ushort CodeCancel = 3;

    /// <summary>Cancel code: forfeit an accepted duel (answered with 2736 NfyResultFiledPvP).</summary>
    internal const ushort CodeForfeit = 5;

    /// <summary>Length of the 1519 body in bytes.</summary>
    internal const int BodyLength = 16;

    /// <summary>Offset of the u32 duel id within the 1519 body.</summary>
    internal const int DuelIdOffset = 10;

    /// <summary>Offset of the u16 cancel code within the 1519 body.</summary>
    internal const int CodeOffset = 14;

    public CancelFieldPvPHandler(IServiceScopeFactory s, ILogger<CancelFieldPvPHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < BodyLength) return;
        var duelId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(DuelIdOffset));
        var code = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(CodeOffset));

        if (!FieldPvPService.TryGet(duelId, out var duel)
            || (player.EntityId != duel.Challenger && player.EntityId != duel.Target))
        {
            Logger.LogDebug("{Name} cancel code {Code} for duel {Id}, which is not one of theirs", player.Name, code, duelId);
            return;
        }
        if (code is not (CodeCancel or CodeForfeit))
        {
            Logger.LogInformation("{Name} sent duel cancel code {Code} for duel {Id}; not handled", player.Name, code, duelId);
            return;
        }
        if (!FieldPvPService.TryClose(duelId, out _)) return;

        var other = player.EntityId == duel.Challenger ? duel.Target : duel.Challenger;
        foreach (var (sessionId, self) in new[] { (duel.ChallengerSession, duel.Challenger), (duel.TargetSession, duel.Target) })
        {
            var s = Broadcast.GetSession(sessionId);
            if (s is not { IsConnected: true }) continue;
            if (code == CodeCancel)
                await s.SendInternalRmiAsync(ZoneS2COpcodes.NfyCancelFiledPvP,
                    FieldPvPPackets.BuildCancel2738(self, duelId, player.EntityId), ct);
            else
                await s.SendInternalRmiAsync(ZoneS2COpcodes.NfyResultFiledPvP,
                    FieldPvPPackets.BuildResult2736(self, other, player.EntityId, FieldPvPPackets.ResultForfeit), ct);
        }

        
        
        
        
        
        
        
        if (code == CodeForfeit)
            await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, other, player.EntityId);
        else
            await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, duel.Challenger, duel.Target);

        Logger.LogInformation("{Name} {Action} duel {Id}", player.Name, code == CodeCancel ? "cancelled" : "forfeited", duelId);
    }
}
