using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1538 ResponseHShieldMsg: the HackShield anti-cheat response. The response is accepted and logged.
/// </summary>
public class HShieldMsgHandler : ZoneRmiHandlerBase<HShieldMsgHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ResponseHShieldMsg;
    public override string Name => "HShieldMsg";

    public HShieldMsgHandler(IServiceScopeFactory s, ILogger<HShieldMsgHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        Logger.LogDebug("{Name}: HShield response ({Size}B) - accepted", player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
