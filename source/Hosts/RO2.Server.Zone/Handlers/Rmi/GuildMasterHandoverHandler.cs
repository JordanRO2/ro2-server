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

/// <summary>RMI 1470 ReqGuildMasterHandover: transfers the guild master role to another member.</summary>
/// <remarks>
/// The request holds the guild GUID (16 bytes) and the new master's character ID (8 bytes). The requester receives a 2635 result. On success the guild receives a 2645 NfyGuildMasterHandover, with layout [8-byte new master ID][8-byte old master ID][u8 0][u8 new rank of the old master], and nearby players receive a 2591 update for both masters. The former master's 2591 carries the guild's lowest title, the rank <see cref="GuildService.SetMasterAsync"/> assigns. No refusal sends a 2645 or a 2591.
/// </remarks>
public class GuildMasterHandoverHandler : ZoneRmiHandlerBase<GuildMasterHandoverHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildMasterHandover;
    public override string Name => "GuildMasterHandover";

    
    
    
    
    
    
    
    
    
 /// <summary>Result 7: no guild was found.</summary>
    internal const ushort ResultGuildNotFound = 7;

 /// <summary>Result 13: the target is not a guild member.</summary>
    internal const ushort ResultTargetNotMember = 13;

 /// <summary>Result 17: the requester lacks the guild master right.</summary>
    internal const ushort ResultNotMaster = 17;

    public GuildMasterHandoverHandler(IServiceScopeFactory s, ILogger<GuildMasterHandoverHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Returns the refusal result for a handover by <paramref name="requesterCharId"/> when the guild is missing or the requester is not its master, or 0 when the handover may proceed to <see cref="GuildService.SetMasterAsync"/>.
 /// </summary>
    internal static ushort Precheck(Guild? guild, uint requesterCharId)
    {
        if (guild == null) return ResultGuildNotFound;
        lock (guild.Gate)
            return guild.MasterCharId == requesterCharId ? (ushort)0 : ResultNotMaster;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 24) return;
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var span = payload.Span;
        ulong newMasterGid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ulong oldMasterGid = guild != null ? GuildService.GidFor(guild.MasterCharId) : 0;
        ushort result = Precheck(guild, player.CharacterId);
        if (result == 0 && !await GuildService.Instance.SetMasterAsync(guild!, newMasterGid))
            result = ResultTargetNotMember;   

        Logger.LogInformation("Session {Id}: {Name} hands guild master to {Gid} -> result {Result}",
            session.SessionId, player.Name, newMasterGid, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildMasterHandover, writer => writer.WriteUInt16(result));

        if (result != 0 || guild == null) return;

        
        
        byte oldMasterRank;
        uint newMasterCharId;
        lock (guild.Gate)
        {
            oldMasterRank = guild.Members.FirstOrDefault(m => m.Gid == oldMasterGid)?.AuthId
                            ?? GuildService.LowestRankIndex(guild);
            newMasterCharId = guild.MasterCharId;
        }
        var body = GuildPackets.BuildNfyGuildMasterHandover(newMasterGid, oldMasterGid, oldMasterRank);
        await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildMasterHandover, body);

        
        
        
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();
        await PcViewInfoPackets.BroadcastToViewersAsync(player, guild, session, Broadcast, emblems);

        if (newMasterCharId != player.CharacterId
            && PlayerManager.GetPlayerByCharacterId(newMasterCharId) is { } heir
            && Broadcast.GetSession(heir.SessionId) is { } heirSession)
            await PcViewInfoPackets.BroadcastToViewersAsync(heir, guild, heirSession, Broadcast, emblems);
    }
}
