using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1587 ReqGuildStorage: requests the contents of the guild vault.</summary>
/// <remarks>
/// <para>
/// The answer is 2856 AnsGuildStorage, which uses the same layout as 2674: [u16 0][u32 length - 6][u64 funds][u16 count][count records of 126 bytes]. The records are the guild's stored items in ascending slot order, and funds are the guild's stored zeny (<see cref="Guild.Gold"/>).
/// </para>
/// <para>
/// Every request is answered with the full vault. The body is always the requester's own guild, resolved from the character. The guild window's storage tab sends this request.
/// </para>
/// </remarks>
public class GuildStorageInfoHandler : ZoneRmiHandlerBase<GuildStorageInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildStorage;
    public override string Name => "GuildStorageInfo";
    public GuildStorageInfoHandler(IServiceScopeFactory s, ILogger<GuildStorageInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        var body = guild != null
            ? GuildPackets.BuildGuildStorageList(guild)
            : GuildPackets.BuildGuildStorageList(new Guild());
        Logger.LogDebug("{Name} requests guild storage info ({Count} items)", player.Name, guild?.Storage.Count ?? 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildStorage, (ReadOnlyMemory<byte>)body);
    }
}
