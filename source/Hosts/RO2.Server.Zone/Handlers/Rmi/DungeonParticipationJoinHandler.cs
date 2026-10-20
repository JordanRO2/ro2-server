using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1576 ReqQuestionAnswer_DungeonParticipationJoin, the player's answer to a dungeon participation prompt.</summary>
/// <remarks>
/// The request body is a u8 answer: 1 for yes and 0 for no. Dungeon-finder group matching is not implemented, so the
/// server never raises this prompt and the answer is only logged. No reply is sent.
/// </remarks>
public class DungeonParticipationJoinHandler : ZoneRmiHandlerBase<DungeonParticipationJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestionAnswer_DungeonParticipationJoin;
    public override string Name => "DungeonParticipationJoin";
    public DungeonParticipationJoinHandler(IServiceScopeFactory s, ILogger<DungeonParticipationJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        int answer = payload.Length >= 1 ? payload.Span[0] : -1;
        Logger.LogInformation(
            "{Name} answers a dungeon participation prompt ({Answer}) that this server never raised; group dungeon-finder matching is not implemented",
            player.Name, answer switch { 1 => "yes", 0 => "no", -1 => "empty body", _ => $"0x{answer:X2}" });
        return Task.CompletedTask;
    }
}
