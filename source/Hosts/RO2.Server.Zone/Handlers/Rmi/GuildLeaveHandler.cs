using RO2.Application.Interfaces;
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
/// RMI 1432 ReqGuildUnregisterMe: the character leaves the guild. The request body is empty.
/// </summary>
/// <remarks>
/// Sends a 2572 acknowledgement, moves the leaver's contribution to the guild master (2730, sent to the master only), and notifies the remaining members with a 2571 NfyGuildMemberRemove (reason 3, left). A guild-exit skill bar refresh precedes the 2572. The guild master cannot leave; the master must hand the guild over (1470) or disband it (1427). A successful leave starts the one-hour rejoin cooldown recorded by <see cref="IGuildDepartureRepository"/>, which the 1429 invite checks.
/// </remarks>
public class GuildLeaveHandler : ZoneRmiHandlerBase<GuildLeaveHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildUnregisterMe;
    public override string Name => "GuildLeave";

    public GuildLeaveHandler(IServiceScopeFactory s, ILogger<GuildLeaveHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Result 2: the guild master cannot leave the guild.</summary>
    private const ushort ResultMasterCannotLeave = 2;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = 0;
        GuildMember? left = null;
        if (guild == null) result = 1;
        
        
        
        
        else if (guild.MasterCharId == player.CharacterId) result = ResultMasterCannotLeave;
        else
        {
            left = await GuildService.Instance.RemoveMemberByCharIdAsync(guild, player.CharacterId);
            if (left == null) result = 1;
            else
            {
                
                
                
                var departures = services.GetRequiredService<IGuildDepartureRepository>();
                await departures.InitAsync();
                await departures.RecordLeaveAsync(player.CharacterId, DateTime.UtcNow);
            }
        }

        Logger.LogInformation("Session {Id}: {Name} leaves guild -> result {Result}", session.SessionId, player.Name, result);

        
        
        
        if (result == 0)
            await GuildSkillBarRefresh.SendAsync(session, player, services, Logger);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildUnregisterMe, writer => writer.WriteUInt16(result));

        if (result == 0 && guild != null && left != null)
        {
            if (guild.Members.Count == 0)
            {
                
                
                await GuildService.Instance.DisbandAsync(guild);
                await emblems.InitAsync();
                await emblems.RemoveAsync(guild.Guid16);
            }
            else
            {
                
                
                
                if (await GuildService.Instance.HandOverContributionAsync(guild, left) is { } masterTotal
                    && PlayerManager.GetPlayerByCharacterId(guild.MasterCharId) is { } master
                    && Broadcast.GetSession(master.SessionId) is { } masterSession)
                {
                    await masterSession.SendRmiAsync(ZoneS2COpcodes.NfyGuildHandoverMemberBribePoint_ToGuildMaster,
                        writer => writer.WriteBytes(GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(masterTotal)));
                }

                
                
                var body = GuildPackets.BuildNfyGuildMemberRemove(guild, left, reason: 3, actorGid: left.Gid);
                await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildMemberRemove, body);
            }

            
            
            
            
            await PcViewInfoPackets.BroadcastToViewersAsync(player, null, session, Broadcast, emblems);
        }
    }
}
