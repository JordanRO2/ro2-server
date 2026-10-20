using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1464 ReqGuildAuthority - the caller's guild rank table, answered with 2628.</summary>
public class GuildAuthorityHandler : ZoneRmiHandlerBase<GuildAuthorityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildAuthority;
    public override string Name => "GuildAuthority";
    public GuildAuthorityHandler(IServiceScopeFactory s, ILogger<GuildAuthorityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        
        
        
        
        
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        Logger.LogDebug("{Name} requests guild authority list ({Count} titles)", player.Name, guild?.Ranks.Count ?? 0);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildAuthority,
            (ReadOnlyMemory<byte>)GuildRosterPackets.BuildAnsGuildAuthority(guild));
    }
}
