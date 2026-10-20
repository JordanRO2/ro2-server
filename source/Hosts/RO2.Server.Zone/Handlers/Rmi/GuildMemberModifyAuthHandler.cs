using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1469 ReqGuildMemeberModifyAuth: changes a member's authority (rank) within the guild.</summary>
/// <remarks>
/// The request is 25 bytes: [16-byte guild GUID][u8 new authority ID][8-byte member ID]. The requester receives a 2634 acknowledgement, and the guild receives a 2644 NfyGuildMemberModifyAuthority with layout [u8 authority ID][8-byte member ID].
/// </remarks>
public class GuildMemberModifyAuthHandler : ZoneRmiHandlerBase<GuildMemberModifyAuthHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildMemeberModifyAuth;
    public override string Name => "GuildMemberModifyAuth";
    public GuildMemberModifyAuthHandler(IServiceScopeFactory s, ILogger<GuildMemberModifyAuthHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 25) return;
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var span = payload.Span;
        byte newAuthId = span[16];
        ulong targetGid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(17, 8));

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = 0;
        if (guild == null) result = 1;
        else if (guild.MasterCharId != player.CharacterId) result = 2;
        else if (!await GuildService.Instance.SetAuthAsync(guild, targetGid, newAuthId)) result = 3;

        Logger.LogInformation("Session {Id}: {Name} sets member {Gid} authority {Auth} -> result {Result}",
            session.SessionId, player.Name, targetGid, newAuthId, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildMemberModifyAuthority, writer => writer.WriteUInt16(result));

        if (result == 0 && guild != null)
        {
            var body = GuildPackets.BuildNfyGuildMemberModifyAuthority(targetGid, newAuthId);
            await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildMemberModifyAuthority, body);
        }
    }
}
