using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1424 ReqMyGuildInfo — return the caller's guild.
/// Both branches send the measured 2559 AnsMyGuildInfo body (NONE family): the real guild's state when
/// the caller has one, and the retail 277-byte all-empty structure when they do not. Retail never
/// answers 1424 with a short stub — every guildless AnsMyGuildInfo is the
/// full structure with an all-zero GUID, nameLen 0 and noticeLen 0.
/// </summary>
public class GuildInfoHandler : ZoneRmiHandlerBase<GuildInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMyGuildInfo;
    public override string Name => "GuildInfo";

    public GuildInfoHandler(IServiceScopeFactory s, ILogger<GuildInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);
        
        
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var departures = services.GetRequiredService<IGuildDepartureRepository>();
        await departures.InitAsync();
        if (await departures.TakeKickNoticeAsync(player.CharacterId) && guild == null)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildKickMember, (ReadOnlyMemory<byte>)GuildDepartureRepository.KickNoticeBody());
            await GuildSkillBarRefresh.SendAsync(session, player, services, Logger);
            Logger.LogInformation("Session {Id}: {Player} was kicked from their guild while offline; sent the 2570 notice",
                session.SessionId, player.Name);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        await PcViewInfoPackets.BroadcastToViewersAsync(player, guild, session, Broadcast, emblems);

        if (guild != null)
        {
            
            
            var store = services.GetRequiredService<RO2.GameData.GameDataStore>();
            await session.SendRmiAsync(ZoneS2COpcodes.AnsMyGuildInfo, (ReadOnlyMemory<byte>)GuildRosterPackets.BuildAnsMyGuildInfo(
                guild, player.CharacterId, cid => GuildRosterPackets.IsOnline(PlayerManager, cid), emblems, store.GuildExpTable));
            return;
        }

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMyGuildInfo,
            (ReadOnlyMemory<byte>)GuildPackets.BuildAnsMyGuildInfoNoGuild());
    }
}
