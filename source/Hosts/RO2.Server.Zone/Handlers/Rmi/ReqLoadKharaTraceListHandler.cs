using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

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

public class ReqLoadKharaTraceListHandler : Base1435
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqLoadKharaTraceList;
    public override string Name => "ReqLoadKharaTraceList";
    public ReqLoadKharaTraceListHandler(IServiceScopeFactory s, ILogger<ReqLoadKharaTraceListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} ({Size}b)", session.SessionId, Name, payload.Length);
        
        
        
        
        
        
        
        
        var player = services.GetRequiredService<RO2.Server.Zone.Services.PlayerStateManager>()
            .GetPlayer(session.SessionId);
        var ids = player is null
            ? (IReadOnlyList<uint>)System.Array.Empty<uint>()
            : await services.GetRequiredService<RO2.Application.Interfaces.IKharaTraceRepository>()
                .GetAsync(player.CharacterId, ct);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsLoadKharaTraceList, writer =>
        {
            var pos = writer.Reserve(4);              
            var start = writer.Length;
            writer.WriteUInt16((ushort)ids.Count);    
            foreach (var id in ids) writer.WriteUInt32(id);
            writer.WriteAt(pos, (uint)(writer.Length - start));
        }, ct);
        Logger.LogDebug("Session {Id}: sent AnsLoadKharaTraceList (2780) with {N} traced Khara",
            session.SessionId, ids.Count);
    }
}
