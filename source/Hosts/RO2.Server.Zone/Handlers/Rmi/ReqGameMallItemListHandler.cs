using RO2.Application.Interfaces;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

public class ReqGameMallItemListHandler : Base1593
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallItemList;
    public override string Name => "ReqGameMallItemList";
    public ReqGameMallItemListHandler(IServiceScopeFactory s, ILogger<ReqGameMallItemListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 1)
        {
            Logger.LogDebug("Session {Id}: {Name} without its u8 list selector — ignored", session.SessionId, Name);
            return;
        }
        byte list = payload.Span[0];

        
        
        
        
        
        
        
        
        
        
        
        IReadOnlyList<uint> ids = Array.Empty<uint>();
        if (list == GameMallListStore.AccountList && session.AccountId is uint accountId)
        {
            try
            {
                ids = await services.GetRequiredService<IGameMallListRepository>().GetAsync(accountId, ct);
            }
            catch (Exception ex)
            {
                
                
                Logger.LogWarning(ex, "Game mall list 2 lookup failed for account {Account}", accountId);
            }
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallItemList, writer => WriteItemList2865(writer, list, ids), ct);
    }

 /// <summary>
 /// Builds the 2865 AnsGameMallItemList body: <c>[u8 list echo][u16 result 0][CompactLen n][n x u32 item id]</c>.
 /// An empty list is the 5-byte form.
 /// </summary>
    internal static void WriteItemList2865(PacketWriter writer, byte list, IReadOnlyList<uint> ids)
    {
        writer.WriteByte(list);              
        writer.WriteUInt16(0);               
        writer.WriteCompactLength(ids.Count);
        foreach (var id in ids) writer.WriteUInt32(id);
    }
}
