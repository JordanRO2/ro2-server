using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1633 ReqGuildSelectRobe: the guild master selects the robe the guild wears. The answer is 2928 AnsGuildSelectRobe, followed on success by a 2927 NfyGuildRobeChanged.
/// </summary>
/// <remarks>
/// <para>
/// The robe is identified by the item ID of the selected entry in the 2929 robe list. Only the guild master receives a code-0 2929 and can open the robe selection window (see <see cref="GuildListRegisteredRobeHandler"/>).
/// </para>
/// <para>
/// Every guild currently lists only the Basic Robe, so the only robe a master can select is the Basic Robe. A non-master requester receives 2928 with <see cref="GuildRobeChangedPackets.SelectNotMaster"/> and no 2927. A request for any other robe ID receives no answer.
/// </para>
/// </remarks>
public class GuildSelectRobeHandler : ZoneRmiHandlerBase<GuildSelectRobeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSelectRobe;
    public override string Name => "GuildSelectRobe";
    public GuildSelectRobeHandler(IServiceScopeFactory s, ILogger<GuildSelectRobeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length != 4)
        {
            Logger.LogWarning("{Name}: 1633 body is {Len} bytes (the client writes 4), ignored", player.Name, payload.Length);
            return;
        }
        uint robeItemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild == null || guild.MasterCharId != player.CharacterId)
        {
            Logger.LogWarning("{Name} asked to select guild robe {Robe} without being a guild master", player.Name, robeItemId);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
            return;
        }

        if (robeItemId != GuildRobeRecord.BasicRobeItemId)
        {
            Logger.LogWarning("{Name} asked to select guild robe {Robe}, which guild {Guild} has not registered (only the Basic Robe {Basic} is)",
                player.Name, robeItemId, guild.Name, GuildRobeRecord.BasicRobeItemId);
            return;
        }

        Logger.LogInformation("{Name} selects the Basic Robe for guild {Guild}", player.Name, guild.Name);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
            GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectAccepted), ct);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildRobeChanged,
            GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(GuildRobeChangedPackets.BasicRobeSelectedRecord()), ct);
    }
}
