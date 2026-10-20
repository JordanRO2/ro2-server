using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1607 ReqColosseumJoin: the player asks to enter the Colosseum. The request has an empty body.
/// </summary>
/// <remarks>
/// The request is logged and not answered. The Colosseum match (rounds, scoring and the return trip) is not
/// implemented, so no entry is granted and no refusal code is defined for this request.
/// </remarks>
public class ColosseumJoinHandler : ZoneRmiHandlerBase<ColosseumJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqColosseumJoin;
    public override string Name => "ColosseumJoin";
    public ColosseumJoinHandler(IServiceScopeFactory s, ILogger<ColosseumJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} asked to enter the Colosseum; no colosseum match is run here, so no reply",
            player.Name);
        return Task.CompletedTask;
    }
}
