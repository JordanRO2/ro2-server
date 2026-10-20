using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqEndScene by replying with an empty AnsEndScene (2684).</summary>
/// <remarks>
/// AnsEndScene has no fields, so the reply body is empty.
/// </remarks>
public class EndSceneHandler : ZoneRmiHandlerBase<EndSceneHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEndScene;
    public override string Name => "EndScene";
    public EndSceneHandler(IServiceScopeFactory s, ILogger<EndSceneHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await session.SendRmiAsync(ZoneS2COpcodes.AnsEndScene, ReadOnlyMemory<byte>.Empty, ct);
    }
}
