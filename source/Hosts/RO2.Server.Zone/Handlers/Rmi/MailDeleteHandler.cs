using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1398 (ReqMailDelete): deletes a mail from the reader's inbox.</summary>
public class MailDeleteHandler : ZoneRmiHandlerBase<MailDeleteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailDelete;
    public override string Name => "MailDelete";
    public MailDeleteHandler(IServiceScopeFactory s, ILogger<MailDeleteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!MailPackets.TryParseMailRef(payload.Span, out var owner, out var mailUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqMailDelete", player.Name);
            return;
        }

        var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
        var deleted = await mail.DeleteMailAsync(player.CharacterId, mailUid);
        Logger.LogInformation("{Name} deletes mail {Uid:X} (deleted={Deleted})", player.Name, mailUid, deleted);

        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnMailDelete, MailPackets.BuildMailDelete2448(player.EntityId, mailUid));
    }
}
