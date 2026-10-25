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
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

public class ReqPCBankDataHandler : Base1340
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCBankData; 
    public override string Name => "ReqPCBankData";
    public ReqPCBankDataHandler(IServiceScopeFactory s, ILogger<ReqPCBankDataHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} ({Size}b)", session.SessionId, Name, payload.Length);
        
        
        
        var pm = services.GetRequiredService<RO2.Server.Zone.Services.PlayerStateManager>();
        if (!pm.TryGetPlayer(session.SessionId, out var player) || player is null) return;
        var inv = services.GetRequiredService<RO2.Inventory.Domain.IInventoryRepository>();
        var bank = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(
            System.Linq.Enumerable.Where(await inv.GetByCharacterAsync(player.CharacterId), i => i.StorageType == 2),
            i => i.Slot));
        var records = new System.Collections.Generic.List<byte[]>(bank.Count);
        foreach (var it in bank)
            records.Add(RO2.Server.Zone.Services.WorldEntryService.BuildItemRecord126(it));
        await session.SendRmiAsync(ZoneS2COpcodes.OnBankData,
            w => w.WriteBytes(RO2.Server.Zone.Services.BankPackets.BuildBankData2338(
                player.EntityId, player.BankGold, records)));
    }
}
