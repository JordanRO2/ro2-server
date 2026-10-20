using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1671 ReqChangeRefineShopName. The request is logged and not answered, because the stock client never
/// sends it and no response is defined for it.
/// </summary>
/// <remarks>
/// The handler stays registered so that a request from a modified client is logged instead of being treated as
/// an unknown opcode.
/// </remarks>
public class ChangeRefineShopNameHandler : ZoneRmiHandlerBase<ChangeRefineShopNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangeRefineShopName;
    public override string Name => "ChangeRefineShopName";

    public ChangeRefineShopNameHandler(IServiceScopeFactory s, ILogger<ChangeRefineShopNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogWarning("{Name}: 1671 ReqChangeRefineShopName of {Len} bytes; the stock client has no builder for 1671, " +
            "not answered", player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
