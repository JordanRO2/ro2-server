using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1367 ReqUseSkillList (ByteArray) - Request active skill list</summary>
public class UseSkillListHandler : ZoneRmiHandlerBase<UseSkillListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUseSkillList;
    public override string Name => "UseSkillList";

    public UseSkillListHandler(IServiceScopeFactory s, ILogger<UseSkillListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests skill list ({Count} skills)", player.Name, player.LearnedSkills.Count);

        
        
        
        
        
        
        
        
        var skills = GuildSkillBookLearn.SkillListFor(player, services.GetRequiredService<GameDataStore>());
        await WorldEntryService.SendUseSkillList2488Async(session, skills);
    }
}
