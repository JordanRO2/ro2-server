using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1518 ReqAcceptFiledPvP, the player's answer to a field duel challenge.
/// </summary>
/// <remarks>
/// Answer codes: 1 accept, 2 refuse, 3 close the duel window (from either player), 4 challenge timed out
/// (from the challenger). An accept from a target farther than <see cref="FieldPvPService.DuelRange"/>
/// from the challenger is refused.
/// The result is sent as NfyAcceptFiledPvP (2737) to both players: 12 accepted, 13 refused, 14 ended,
/// 18 out of range, 28 timed out. An accepted duel is started with a 2758 notification after a short delay.
/// A late answer to a duel that is no longer open receives error 23.
/// </remarks>
public class AcceptFieldPvPHandler : ZoneRmiHandlerBase<AcceptFieldPvPHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAcceptFiledPvP;
    public override string Name => "AcceptFieldPvP";

    internal const ushort AnswerAccept = 1;
    internal const ushort AnswerRefuse = 2;
 /// <summary>Answer code that closes the duel window. Valid from either player.</summary>
    internal const ushort AnswerClose = 3;
    internal const ushort AnswerChallengerTimedOut = 4;

    public AcceptFieldPvPHandler(IServiceScopeFactory s, ILogger<AcceptFieldPvPHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < 24) return;
        var answer = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
        var challenger = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
        var target = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
        var duelId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20));

        if (!FieldPvPService.TryGet(duelId, out var duel)
            || duel.Challenger != challenger || duel.Target != target)
        {
            
            
            
            if (IsLateAnswerFromItsPlayer(answer, player.EntityId, challenger, target))
                await SendAcceptAsync(session, player.EntityId, duelId, challenger, target,
                    0f, 0f, 0f, FieldPvPPackets.AcceptAcknowledged, ct);
            else
                Logger.LogDebug("{Name} answered {Answer} to duel {Id}, which is not open", player.Name, answer, duelId);
            return;
        }

        switch (answer)
        {
            case AnswerAccept when player.EntityId == duel.Target:
                
                
                
                
                
                
                if (duel.State == FieldPvPService.DuelState.Pending
                    && PlayerManager.GetPlayer(duel.ChallengerSession) is { } challengerState
                    && challengerState.EntityId == duel.Challenger && challengerState.MapId == duel.MapId
                    && !FieldPvPService.WithinDuelRange(player, challengerState))
                {
                    if (!FieldPvPService.TryClose(duel.Id, out _)) return;
                    await NotifyBothAsync(duel, 0f, 0f, 0f, FieldPvPPackets.AcceptTooFar, ct);
                    await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, duel.Challenger, duel.Target);
                    Logger.LogInformation("{Name} accepted duel {Id} out of range ({Dist:F2} > {Range}); refused with 18",
                        player.Name, duel.Id,
                        MathF.Sqrt((player.X - challengerState.X) * (player.X - challengerState.X)
                                   + (player.Y - challengerState.Y) * (player.Y - challengerState.Y)),
                        FieldPvPService.DuelRange);
                    return;
                }
                if (!FieldPvPService.TryAccept(duel)) return;
                
                await NotifyBothAsync(duel, player.X, player.Y, player.Z, FieldPvPPackets.AcceptAccepted, ct);
                Logger.LogInformation("{Name} accepted duel {Id}", player.Name, duel.Id);
                
                
                _ = StartDuelAfterCountdownAsync(duel);
                break;

            case AnswerRefuse when player.EntityId == duel.Target:
                if (!FieldPvPService.TryClose(duel.Id, out _)) return;
                await NotifyBothAsync(duel, 0f, 0f, 0f, FieldPvPPackets.AcceptRefused, ct);
                await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, duel.Challenger, duel.Target);
                Logger.LogInformation("{Name} refused duel {Id}", player.Name, duel.Id);
                break;

            case AnswerClose when IsPlayerOf(duel, player.EntityId):
                
                
                
                if (FieldPvPService.TryClose(duel.Id, out _))
                {
                    await NotifyBothAsync(duel, 0f, 0f, 0f, FieldPvPPackets.AcceptWithdrawn, ct);
                    await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, duel.Challenger, duel.Target);
                    Logger.LogInformation("{Name} closed duel {Id} (was {State})", player.Name, duel.Id, duel.State);
                }
                else
                {
                    await SendAcceptAsync(session, player.EntityId, duel.Id, duel.Challenger, duel.Target,
                        0f, 0f, 0f, FieldPvPPackets.AcceptAcknowledged, ct);
                }
                break;

            case AnswerChallengerTimedOut when player.EntityId == duel.Challenger:
                
                
                if (!FieldPvPService.TryClose(duel.Id, out _)) return;
                await SendAcceptAsync(session, player.EntityId, duel.Id, duel.Challenger, duel.Target,
                    0f, 0f, 0f, FieldPvPPackets.AcceptTimedOut, ct);
                await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, duel.Challenger, duel.Target);
                Logger.LogInformation("{Name}'s duel challenge {Id} timed out (state {State})",
                    player.Name, duel.Id, duel.State);
                break;

            default:
                Logger.LogInformation("{Name} sent duel answer {Answer} for duel {Id} (state {State}); not handled",
                    player.Name, answer, duel.Id, duel.State);
                break;
        }
    }

 /// <summary>
 /// Whether <paramref name="answer"/> is a valid late answer from <paramref name="sender"/> for a duel that
 /// is no longer open: 1 or 2 from the target, 3 from either player, 4 from the challenger.
 /// </summary>
    internal static bool IsLateAnswerFromItsPlayer(ushort answer, uint sender, uint challenger, uint target)
        => answer switch
        {
            AnswerAccept or AnswerRefuse => sender == target,
            AnswerClose => sender == challenger || sender == target,
            AnswerChallengerTimedOut => sender == challenger,
            _ => false,
        };

 /// <summary>Whether <paramref name="entityId"/> is one of the duel's two players.</summary>
    internal static bool IsPlayerOf(FieldPvPService.Duel duel, uint entityId)
        => entityId == duel.Challenger || entityId == duel.Target;

 /// <summary>The same 2737 to both players, each with its own handle first.</summary>
    private async Task NotifyBothAsync(FieldPvPService.Duel duel, float x, float y, float z, ushort result,
        CancellationToken ct)
    {
        foreach (var (sessionId, self) in new[] { (duel.ChallengerSession, duel.Challenger), (duel.TargetSession, duel.Target) })
        {
            var s = Broadcast.GetSession(sessionId);
            if (s is { IsConnected: true })
                await SendAcceptAsync(s, self, duel.Id, duel.Challenger, duel.Target, x, y, z, result, ct);
        }
    }

 /// <summary>
 /// Sends the start notification (2758) to both duelists after the countdown, if the duel is still the
 /// accepted one and both players remain on its map.
 /// </summary>
 /// <remarks>
 /// A duel whose player left the world or map during the countdown is not started.
 /// </remarks>
    private async Task StartDuelAfterCountdownAsync(FieldPvPService.Duel duel)
    {
        try
        {
            await Task.Delay(FieldPvPStartPackets.StartDelay);

            FieldPvPService.TryGet(duel.Id, out var current);
            if (!FieldPvPStartPackets.StillStarts(duel, current))
            {
                Logger.LogDebug("Duel {Id} ended before its start; no 2758", duel.Id);
                return;
            }
            if (!IsOnDuelMap(duel, duel.ChallengerSession, duel.Challenger)
                || !IsOnDuelMap(duel, duel.TargetSession, duel.Target))
            {
                Logger.LogInformation("Duel {Id}: a player left map {Map} before the start; no 2758", duel.Id, duel.MapId);
                return;
            }

            
            
            if (!FieldPvPService.TryMarkStarted(duel))
            {
                Logger.LogDebug("Duel {Id} ended just before its start; no 2758", duel.Id);
                return;
            }

            
            
            
            await FieldPvPStatePackets.BroadcastStartPairAsync(Broadcast, duel);

            foreach (var (sessionId, self) in new[] { (duel.ChallengerSession, duel.Challenger), (duel.TargetSession, duel.Target) })
            {
                var s = Broadcast.GetSession(sessionId);
                if (s is { IsConnected: true })
                    await s.SendInternalRmiAsync(ZoneS2COpcodes.NfyStartFiledPvP, FieldPvPStartPackets.BuildStart2758(self));
            }
            Logger.LogInformation("Duel {Id} started", duel.Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Duel {Id}: sending the 2758 start failed", duel.Id);
        }
    }

 /// <summary>Whether the duel's player on <paramref name="sessionId"/> is still that character, on the duel's map.</summary>
    private bool IsOnDuelMap(FieldPvPService.Duel duel, uint sessionId, uint entityId)
        => PlayerManager.GetPlayer(sessionId) is { } p && p.EntityId == entityId && p.MapId == duel.MapId;

    private static Task SendAcceptAsync(ProudSession s, uint self, uint duelId, uint challenger, uint target,
        float x, float y, float z, ushort result, CancellationToken ct)
        => s.SendInternalRmiAsync(ZoneS2COpcodes.NfyAcceptFiledPvP,
            FieldPvPPackets.BuildAccept2737(self, duelId, challenger, target, x, y, z, result), ct);
}
