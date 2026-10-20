using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1634 ReqGuildListRegisteredRobe: requests the guild's registered robes for the robe window.</summary>
/// <remarks>
/// The request body is empty. The 2929 answer depends on the requester: the guild master receives the robe list (code 0, at least one entry, starting with the Basic Robe), and other members receive code 0x13 with an empty record, which does not open the window. See <see cref="GuildPackets.BuildAnsGuildListRegisteredRobeBasic"/> and <see cref="GuildPackets.BuildAnsGuildListRegisteredRobeEmpty"/>.
/// </remarks>
public class GuildListRegisteredRobeHandler : ZoneRmiHandlerBase<GuildListRegisteredRobeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildListRegisteredRobe;
    public override string Name => "GuildListRegisteredRobe";
    public GuildListRegisteredRobeHandler(IServiceScopeFactory s, ILogger<GuildListRegisteredRobeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);

        // The result code is keyed on the viewer, not on the guild: code 0 goes to the guild master
        // (rank 0) and 0x13 to every other rank, members of robe-owning guilds included.
        var isMaster = guild != null && guild.MasterCharId == player.CharacterId;

        // This server has no robe purchase, selection or recolouring, so every guild is in the state of a
        // guild that never bought a robe: the master gets the Basic Robe as both the current robe and the
        // one registered robe.
        var body = isMaster
            ? GuildPackets.BuildAnsGuildListRegisteredRobeBasic()
            : GuildPackets.BuildAnsGuildListRegisteredRobeEmpty();

        Logger.LogDebug("{Name} requests the registered guild robe list ({Form})",
            player.Name, isMaster ? "master: Basic Robe" : "not the master: 0x13");

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildListRegisteredRobe, (ReadOnlyMemory<byte>)body);
    }
}
