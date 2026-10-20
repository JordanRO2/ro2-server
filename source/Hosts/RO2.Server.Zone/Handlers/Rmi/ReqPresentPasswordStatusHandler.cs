using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using Base1304 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.AnsMapLoadingHandler>;
using Base1307 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqActorDetailInfoHandler>;
using Base1340 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPCBankDataHandler>;
using Base1435 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadKharaTraceListHandler>;
using Base1442 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqCardBookListHandler>;
using Base1513 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGuildSkillListHandler>;
using Base1516 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMessengerLoginKeyHandler>;
using Base1541 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadQuestTraceListHandler>;
using Base1567 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMyEventHandler>;
using Base1592 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListAllHandler>;
using Base1593 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListHandler>;
using Base1596 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallMyInfoHandler>;
using Base1599 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPresentPasswordStatusHandler>;
using Base1642 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPlayTimeRewardHandler>;
using Base1644 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqAttendanceRewardHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

public class ReqPresentPasswordStatusHandler : Base1599
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPresentPasswordStatus;
    public override string Name => "ReqPresentPasswordStatus";
    public ReqPresentPasswordStatusHandler(IServiceScopeFactory s, ILogger<ReqPresentPasswordStatusHandler> l) : base(s, l) { }

 /// <summary>Status byte for an account that has a gift password.</summary>
    private const byte StatusPasswordSet = 0;

 /// <summary>Status byte for an account without a gift password.</summary>
    private const byte StatusNoPassword = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        bool hasPassword = true;
        if (session.AccountId is uint accountId)
        {
            try
            {
                hasPassword = await services.GetRequiredService<IPresentPasswordRepository>()
                    .HasPasswordAsync(accountId, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Present password status lookup failed for account {Account}", accountId);
            }
        }
        else
        {
            Logger.LogWarning("Session {SessionId}: 1599 before ZoneAuth bound an account", session.SessionId);
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsPresentPasswordStatus, writer =>
        {
            writer.WriteByte(hasPassword ? StatusPasswordSet : StatusNoPassword);
        }, ct);
    }
}
