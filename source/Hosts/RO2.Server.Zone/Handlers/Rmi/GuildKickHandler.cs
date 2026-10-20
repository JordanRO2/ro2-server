using RO2.Application.Interfaces;
using System.Buffers.Binary;
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
/// RMI 1431 ReqGuildKickMember: removes a member from the guild by name.
/// </summary>
/// <remarks>
/// Allowed for the guild master and for members whose title grants the member-removal right (<c>GuildService.PermissionKick</c>). The kicker receives a 2569 result. On success the master receives a 2730 with the updated contribution total, the remaining members receive a 2571 NfyGuildMemberRemove with reason 2 (kicked), and the kicked member receives a 2570 NfyGuildKickMember and a guildless 2591 update. If the kicked member is offline, the 2570 is sent at their next world entry (see <see cref="IGuildDepartureRepository"/>).
/// </remarks>
public class GuildKickHandler : ZoneRmiHandlerBase<GuildKickHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildKickMember;
    public override string Name => "GuildKick";

    public GuildKickHandler(IServiceScopeFactory s, ILogger<GuildKickHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int NameFieldOff = 4;
    private const int NameFieldBytes = 44;                         
    private const int NameMaxChars = NameFieldBytes / 2;
    private const int TailOff = NameFieldOff + NameFieldBytes;     

 /// <summary>Reason code 2 in the 2571 NfyGuildMemberRemove notice: the member was kicked. Reason 3 means the member left.</summary>
    private const byte RemoveReasonKicked = 2;

    
    
    
    
    
    
    
    
    
 /// <summary>Result 7: the kicker is not in a guild.</summary>
    internal const ushort ResultNoGuild = 7;

 /// <summary>Result 8: the kicker lacks the member-removal right, or the target is the guild master.</summary>
    internal const ushort ResultNoRight = 8;

 /// <summary>Result 24: the kicker targeted themselves.</summary>
    internal const ushort ResultSelf = 24;

 /// <summary>Result 10: no member of the kicker's guild has the given name.</summary>
    internal const ushort ResultNoSuchMember = 10;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();

        var targetName = payload.Length >= 6 ? GuildPackets.ReadWideNameAt(payload.Span, NameFieldOff, NameMaxChars) : string.Empty;

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = 0;
        GuildMember? kicked = null;

        if (guild == null) result = ResultNoGuild;
        
        else if (!GuildService.HasPermission(guild, player.CharacterId, GuildService.PermissionKick)) result = ResultNoRight;
        else if (string.Equals(targetName, player.Name, StringComparison.OrdinalIgnoreCase)) result = ResultSelf;
        else
        {
            GuildMember? targetMember;
            lock (guild.Gate)
                targetMember = guild.Members.FirstOrDefault(m => string.Equals(m.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (targetMember == null) result = ResultNoSuchMember;
            
            
            else if (targetMember.CharId == guild.MasterCharId) result = ResultNoRight;
            else
            {
                kicked = await GuildService.Instance.RemoveMemberByCharIdAsync(guild, targetMember.CharId);
                if (kicked == null) result = ResultNoSuchMember;   
            }
        }

        Logger.LogInformation("Session {Id}: {Name} kicks '{Target}' -> result {Result}",
            session.SessionId, player.Name, targetName, result);

        var ack = BuildAnsGuildKickMember(result, guild, payload.Span, targetName);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildKickMember, (ReadOnlyMemory<byte>)ack);

        if (result == 0 && guild != null && kicked != null)
        {
            
            
            
            
            
            
            
            
            
            
            if (await GuildService.Instance.HandOverContributionAsync(guild, kicked) is { } masterTotal
                && PlayerManager.GetPlayerByCharacterId(guild.MasterCharId) is { } master
                && Broadcast.GetSession(master.SessionId) is { } masterSession)
            {
                await masterSession.SendRmiAsync(ZoneS2COpcodes.NfyGuildHandoverMemberBribePoint_ToGuildMaster,
                    (ReadOnlyMemory<byte>)GuildHandoverPackets.BuildNfyHandoverMemberBribePointToMaster(masterTotal));
            }

            
            
            
            
            
            
            var kickerGid = guild.Members.FirstOrDefault(m => m.CharId == player.CharacterId)?.Gid
                            ?? GuildService.GidFor(player.CharacterId);
            var body = GuildPackets.BuildNfyGuildMemberRemove(guild, kicked, reason: RemoveReasonKicked, actorGid: kickerGid);
            await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildMemberRemove, body);

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            var target = PlayerManager.GetPlayerByCharacterId(kicked.CharId);
            var targetSession = target == null ? null : Broadcast.GetSession(target.SessionId);
            if (target != null && targetSession != null)
            {
                await targetSession.SendRmiAsync(ZoneS2COpcodes.NfyGuildKickMember,
                    (ReadOnlyMemory<byte>)BuildNfyGuildKickMember(guild.Name));
                
                
                
                
                
                
                
                await GuildSkillBarRefresh.SendAsync(targetSession, target, services, Logger);
                await targetSession.SendRmiAsync(ZoneS2COpcodes.NfyPCViewInfoUpdate,
                    (ReadOnlyMemory<byte>)PcViewInfoPackets.BuildNfyPcViewInfoUpdate(target.EntityId, null, kicked.CharId, emblems));
                Logger.LogInformation("Session {Id}: sent 2570 kick notice for guild '{Guild}' to {Target} (session {TargetSession})",
                    session.SessionId, guild.Name, kicked.Name, targetSession.SessionId);
            }
            else
            {
                
                
                
                
                
                
                var departures = services.GetRequiredService<IGuildDepartureRepository>();
                await departures.InitAsync();
                await departures.MarkKickNoticeAsync(kicked.CharId);
                Logger.LogInformation("Session {Id}: {Target} is offline; the 2570 kick notice waits for their next world entry",
                    session.SessionId, kicked.Name);
            }
        }
    }

 /// <summary>Maximum guild-name length in the kick notice. Matches the clamp used by the guild info builder, so the notice shows the name as the guild panel does.</summary>
    private const int KickNoticeGuildNameMaxChars = 24;

 /// <summary>
 /// Builds the 2570 NfyGuildKickMember body: [u32 L = 3 + 2n][u8 n][n UTF-16LE characters of the guild name][u16 0].
 /// </summary>
    internal static byte[] BuildNfyGuildKickMember(string? guildName)
    {
        var name = guildName ?? string.Empty;
        if (name.Length > KickNoticeGuildNameMaxChars) name = name.Substring(0, KickNoticeGuildNameMaxChars);

        using var w = new PacketWriter(8 + name.Length * 2);
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteByte((byte)name.Length);              
        foreach (var c in name) w.WriteUInt16(c);    
        w.WriteUInt16(0);                            
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2569 AnsGuildKickMember body: [u32 64][u16 result][16-byte guild GUID][the request's 44-byte name field and WStr16, echoed]. The GUID is the kicker's guild on success and zero on a refusal. The body is 68 bytes.
 /// </summary>
    internal static byte[] BuildAnsGuildKickMember(ushort result, Guild? guild, ReadOnlySpan<byte> request, string targetName)
    {
        using var w = new PacketWriter(80);
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteUInt16(result);

        var guid = new byte[16];
        if (result == 0 && guild?.Guid16 != null) Array.Copy(guild.Guid16, guid, Math.Min(16, guild.Guid16.Length));
        w.WriteBytes(guid);

        if (request.Length >= TailOff)
        {
            w.WriteBytes(request.Slice(NameFieldOff, NameFieldBytes));
        }
        else
        {
            var field = new byte[NameFieldBytes];
            var n = Math.Min(targetName.Length, NameMaxChars - 1);
            for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(i * 2, 2), targetName[i]);
            w.WriteBytes(field);
        }

        
        ushort tailChars = 0;
        if (request.Length >= TailOff + 2)
        {
            tailChars = BinaryPrimitives.ReadUInt16LittleEndian(request.Slice(TailOff, 2));
            if (TailOff + 2 + tailChars * 2 > request.Length) tailChars = 0;
        }
        w.WriteUInt16(tailChars);
        if (tailChars > 0) w.WriteBytes(request.Slice(TailOff + 2, tailChars * 2));

        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }
}
