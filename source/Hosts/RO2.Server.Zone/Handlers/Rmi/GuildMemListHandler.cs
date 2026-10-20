using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1425 ReqGuildMemList: returns the guild roster.</summary>
/// <remarks>
/// The answer is 2560 AnsGuildMemList: [u32 length - 4][16-byte guild GUID][u16 count][count records of 115 bytes]. The roster returned is always the requester's own guild.
/// </remarks>
public class GuildMemListHandler : ZoneRmiHandlerBase<GuildMemListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildMemList;
    public override string Name => "GuildMemList";
    public GuildMemListHandler(IServiceScopeFactory s, ILogger<GuildMemListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild == null)
        {
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildMemList,
                (ReadOnlyMemory<byte>)GuildRosterPackets.BuildAnsGuildMemListEmpty(payload.Span));
            return;
        }

        var members = await GuildRosterPackets.ResolveMembersAsync(
            guild, PlayerManager, services.GetRequiredService<ICharacterRepository>());
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildMemList,
            (ReadOnlyMemory<byte>)GuildRosterPackets.BuildAnsGuildMemList(guild, members));
    }
}
