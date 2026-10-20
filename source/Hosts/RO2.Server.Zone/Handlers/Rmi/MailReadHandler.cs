using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1397 (ReqMailRead): marks a mail as read and returns its full body.</summary>
public class MailReadHandler : ZoneRmiHandlerBase<MailReadHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailRead;
    public override string Name => "MailRead";
    public MailReadHandler(IServiceScopeFactory s, ILogger<MailReadHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!MailPackets.TryParseMailRef(payload.Span, out var owner, out var mailUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqMailRead", player.Name);
            return;
        }

        var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
        var m = await mail.ReadMailAsync(player.CharacterId, mailUid);

        
        if (m == null)
        {
            
            
            
            
            Logger.LogDebug("{Name} reads mail {Uid:X}: not found", player.Name, mailUid);
            await session.SendRmiAsync(ZoneS2COpcodes.OnMailRead,
                MailPackets.BuildMailReadFailed2446(player.EntityId, MailPackets.ReadInvalidMail));
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        byte[] item126 = MailPackets.BuildAttachmentRecord126(m, MailService.ClaimedThisEntry(player, m.Uid));

        Logger.LogDebug("{Name} reads mail {Uid:X}", player.Name, mailUid);
        await session.SendRmiAsync(ZoneS2COpcodes.OnMailRead,
            MailPackets.BuildMailRead2446(player.EntityId, mailUid, m.IsRead, m.SentAt, m.AuctionPrice, item126,
                m.Body ?? string.Empty));
    }
}
