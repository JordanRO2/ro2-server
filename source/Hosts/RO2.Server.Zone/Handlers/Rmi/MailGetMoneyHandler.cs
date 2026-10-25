using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1457 (ReqMailGetMoney): moves the attached zeny into the reader's wallet.</summary>
public class MailGetMoneyHandler : ZoneRmiHandlerBase<MailGetMoneyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailGetMoney;
    public override string Name => "MailGetMoney";
    public MailGetMoneyHandler(IServiceScopeFactory s, ILogger<MailGetMoneyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!MailPackets.TryParseMailRef(payload.Span, out var owner, out var mailUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqMailGetMoney", player.Name);
            return;
        }

        var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
        var (claimed, granted) = await mail.ClaimGoldAsync(player.CharacterId, mailUid);
        if (granted > 0)
        {
            
            
            player.Credit(granted);
            await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
            Logger.LogInformation("{Name} claims {Gold} zeny from mail {Uid:X}", player.Name, granted, mailUid);
        }

        
        
        
        
        
        
        
        
        
        
        byte disposition = granted > 0 && claimed != null && MailService.ClaimRemovesMail(claimed)
            ? MailPackets.ClaimDeletesMail
            : MailPackets.GetMoneyKeepMail;
        await session.SendRmiAsync(ZoneS2COpcodes.OnMailGetMoney,
            MailPackets.BuildMailGetMoney2617(player.EntityId, mailUid, player.Gold, disposition));
    }
}
