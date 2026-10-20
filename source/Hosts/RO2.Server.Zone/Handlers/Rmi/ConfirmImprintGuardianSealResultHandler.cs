using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1695 ReqConfirmImprintGuardianSealResult: the player's keep or discard choice for a guardian-seal roll.</summary>
/// <remarks>
/// The body is one byte. The choice is logged and otherwise ignored, and no reply is sent: the imprint roll keeps
/// no pending result on the server, so there is no state for the choice to commit.
/// </remarks>
public class ConfirmImprintGuardianSealResultHandler : ZoneRmiHandlerBase<ConfirmImprintGuardianSealResultHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqConfirmImprintGuardianSealResult;
    public override string Name => "ConfirmImprintGuardianSealResult";
    public ConfirmImprintGuardianSealResultHandler(IServiceScopeFactory s, ILogger<ConfirmImprintGuardianSealResultHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 1) return Task.CompletedTask;
        var choice = payload.Span[0];

        Logger.LogDebug("{Name} answers guardian seal imprint result: {Choice} (no pending roll to apply)",
            player.Name, choice);
        return Task.CompletedTask;
    }
}
