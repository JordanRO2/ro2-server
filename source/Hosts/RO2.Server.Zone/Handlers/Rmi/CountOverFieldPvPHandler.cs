using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1520 ReqCountOverFieldPvP: a duelist reports that the out-of-range countdown ran out, which ends the duel with
/// that player as the loser.
/// </summary>
/// <remarks>
/// <para>
/// The request body is a compact message whose duel id is at body offset 10.
/// </para>
/// <para>
/// The request is accepted only for a duel that has started and that the sender is part of. The result is sent as
/// S2C 2736 NfyResultFiledPvP to both players, followed by the end-of-duel state (<see cref="FieldPvPStatePackets"/>).
/// A request for an unknown, pending or foreign duel is logged and not answered.
/// </para>
/// </remarks>
public class CountOverFieldPvPHandler : ZoneRmiHandlerBase<CountOverFieldPvPHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCountOverFieldPvP;
    public override string Name => "CountOverFieldPvP";

 /// <summary>Length of the request body, including its 2-byte compact header.</summary>
    internal const int RequestLength = 14;

 /// <summary>Message tag of the request.</summary>
    internal const ushort MsgTag = 58454;

 /// <summary>Offset of the duel id within the request body.</summary>
    private const int DuelIdOffset = 10;

    public CountOverFieldPvPHandler(IServiceScopeFactory s, ILogger<CountOverFieldPvPHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Reads the duel id of a request body, or returns false when the body is too short or is not this message.
 /// </summary>
    internal static bool TryParseDuelId(ReadOnlySpan<byte> body, out uint duelId)
    {
        duelId = 0;
        if (body.Length < RequestLength || BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4)) != MsgTag)
            return false;
        duelId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(DuelIdOffset));
        return true;
    }

 /// <summary>
 /// Whether a request from <paramref name="sender"/> ends <paramref name="duel"/>: the sender is one of its two
 /// players and the duel was accepted.
 /// </summary>
    internal static bool EndsDuel(FieldPvPService.Duel duel, uint sender)
        => duel.State == FieldPvPService.DuelState.Accepted && (sender == duel.Challenger || sender == duel.Target);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseDuelId(payload.Span, out var duelId))
        {
            Logger.LogWarning("{Name}: 1520 ReqCountOverFieldPvP of {Len} bytes is not the 14-byte 58454 message",
                player.Name, payload.Length);
            return;
        }

        if (!FieldPvPService.TryGet(duelId, out var duel) || !EndsDuel(duel, player.EntityId))
        {
            Logger.LogInformation("{Name} left the range of duel {Id}, which is not a started duel of theirs; no reply",
                player.Name, duelId);
            return;
        }
        if (!FieldPvPService.TryClose(duelId, out _)) return;

        var winner = player.EntityId == duel.Challenger ? duel.Target : duel.Challenger;
        foreach (var (sessionId, self) in new[] { (duel.ChallengerSession, duel.Challenger), (duel.TargetSession, duel.Target) })
        {
            var s = Broadcast.GetSession(sessionId);
            if (s is not { IsConnected: true }) continue;
            await s.SendInternalRmiAsync(ZoneS2COpcodes.NfyResultFiledPvP,
                FieldPvPPackets.BuildResult2736(self, winner, player.EntityId, FieldPvPPackets.ResultOutOfRange), ct);
        }

        
        
        
        await FieldPvPStatePackets.BroadcastEndPairAsync(Broadcast, duel.MapId, winner, player.EntityId);

        Logger.LogInformation("{Name} lost duel {Id} by leaving its range", player.Name, duelId);
    }
}
