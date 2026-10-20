using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1395 (ReqMailClose): the client closed its mailbox window.</summary>
/// <remarks>
/// No reply is sent. The server keeps no per-player "mailbox open" state, so every mail request is served
/// whether or not the window is open.
/// </remarks>
public class MailCloseHandler : ZoneRmiHandlerBase<MailCloseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailClose;
    public override string Name => "MailClose";
    public MailCloseHandler(IServiceScopeFactory s, ILogger<MailCloseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} closes mailbox", player.Name);
        return Task.CompletedTask;
    }
}
