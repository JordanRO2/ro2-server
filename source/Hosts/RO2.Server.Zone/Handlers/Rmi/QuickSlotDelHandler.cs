using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Application.Interfaces;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1318 (ReqQuickSlotDel): removes an item from a quick slot.</summary>
public class QuickSlotDelHandler : ZoneRmiHandlerBase<QuickSlotDelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuickSlotDel;
    public override string Name => "QuickSlotDel";
    public QuickSlotDelHandler(IServiceScopeFactory s, ILogger<QuickSlotDelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!QuickSlotPackets.TryReadRequest(payload.Span, out var actorHandle, out var req))
        {
            Logger.LogWarning("Session {Id}: malformed ReqQuickSlotDel ({Len} bytes)",
                session.SessionId, payload.Length);
            return;
        }

        var repo = services.GetRequiredService<IQuickSlotRepository>();
        var ex = await QuickSlotFlows.ClearAsync(repo, player.CharacterId, req.Page, req.Slot);
        if (ex is not null)
            Logger.LogError(ex, "Session {Id}: could not clear quick slot", session.SessionId);

        await session.SendRmiAsync(ZoneS2COpcodes.OnDelQuickSlot,
            writer => writer.WriteBytes(
                QuickSlotPackets.BuildAck(QuickSlotPackets.MsgTagDelQuickSlot, actorHandle)));
    }
}
