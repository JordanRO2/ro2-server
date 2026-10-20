using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1373 (ReqPCRevivalHelp), a dead player's request for help. The request is logged and not answered.</summary>
/// <remarks>
/// Revival is offered through the revival skill path instead: a revival skill that lands on a dead player sends
/// that player the revival offer. See <see cref="SkillDamageHandler"/> and <see cref="RevivalSkillHandler"/>.
/// </remarks>
public class RevivalHelpHandler : ZoneRmiHandlerBase<RevivalHelpHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCRevivalHelp;
    public override string Name => "RevivalHelp";

    public RevivalHelpHandler(IServiceScopeFactory s, ILogger<RevivalHelpHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests revival help ({Len} bytes): not answered (no retail 1373/2402 on record)",
            player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
