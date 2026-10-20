using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Persistence.Mongo.Persistence;
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

public class ReqGuildSkillListHandler : Base1513
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSkillList;
    public override string Name => "ReqGuildSkillList";
    public ReqGuildSkillListHandler(IServiceScopeFactory s, ILogger<ReqGuildSkillListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        
        
        var players = services.GetRequiredService<PlayerStateManager>();
        Guild? guild = null;
        if (players.TryGetPlayer(session.SessionId, out var player) && player != null)
        {
            await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
            guild = GuildService.Instance.GetByCharId(player.CharacterId);
        }

        
        
        
        
        
        var skills = guild == null
            ? new List<uint>()
            : SnapshotSkills(guild).GroupBy(id => id / 100).Select(line => line.Max()).OrderBy(id => id).ToList();

        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSkillList, writer =>
        {
            writer.WriteUInt16(0); 
            var pos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16((ushort)skills.Count);
            foreach (var id in skills) writer.WriteUInt32(id);
            writer.WriteAt(pos, (uint)(writer.Length - start));
        });

        
        
        
        
        
        
        
        if (guild != null && player != null)
        {
            var store = services.GetRequiredService<GameDataStore>();
            var burst = UseSkillListLedger.PlanGuildLinkBurst(player.LearnedSkills, UseSkillListLedger.HeldBy(session),
                SnapshotSkills(guild), GuildSkillLearnPackets.Index(store.GuildSkillLevel));
            foreach (var list in burst)
                await WorldEntryService.SendUseSkillList2488Async(session, list);   
        }
    }

    private static uint[] SnapshotSkills(Guild g)
    {
        lock (g.Gate) return g.Skills.ToArray();
    }
}
