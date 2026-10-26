using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;
using RO2.Characters.Application;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1319 (ReqQuickSlotAdd): assigns an item or skill to a quick slot.</summary>
public class QuickSlotAddHandler : ZoneRmiHandlerBase<QuickSlotAddHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuickSlotAdd;
    public override string Name => "QuickSlotAdd";
    public QuickSlotAddHandler(IServiceScopeFactory s, ILogger<QuickSlotAddHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        if (!QuickSlotPackets.TryReadRequest(payload.Span, out var actorHandle, out var req))
        {
            Logger.LogWarning("Session {Id}: malformed ReqQuickSlotAdd ({Len} bytes)",
                session.SessionId, payload.Length);
            return;
        }

        
        
        
        var repo = services.GetRequiredService<IQuickSlotRepository>();
        var ex = await QuickSlotFlows.SetAsync(repo, new CharacterQuickSlot
        {
            CharacterId = player.CharacterId,
            Page = req.Page,
            Slot = req.Slot,
            EntryType = req.EntryType,
            EntryId = req.EntryId,
            Amount = req.Amount,
            ItemUid = req.ItemUid,
        });
        if (ex is not null)
            Logger.LogError(ex, "Session {Id}: could not persist quick slot", session.SessionId);

        Logger.LogDebug("{Name} sets quick slot p{Page}s{Slot} = type {Type} id {Id}",
            player.Name, req.Page, req.Slot, req.EntryType, req.EntryId);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAddQuickSlot,
            writer => writer.WriteBytes(
                QuickSlotPackets.BuildAck(QuickSlotPackets.MsgTagAddQuickSlot, actorHandle)));
    }
}
