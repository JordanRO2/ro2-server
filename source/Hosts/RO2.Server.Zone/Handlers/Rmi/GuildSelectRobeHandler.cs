using RO2.Guilds.Domain;
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
/// RMI 1633 ReqGuildSelectRobe: the guild master switches the robe the guild wears. The answer is 2928 AnsGuildSelectRobe, followed on success by a 2927 NfyGuildRobeChanged.
/// </summary>
/// <remarks>
/// <para>
/// The robe is identified by the item ID of the selected entry in the 2929 robe list (see <see cref="GuildListRegisteredRobeHandler"/>). Only the guild master receives a code-0 2929 and can open the robe selection window.
/// </para>
/// <para>
/// Switching is free: the master may wear the Basic Robe (the default) or any robe the guild has already unlocked by
/// buying it at the robe NPC. A robe the guild has not bought is not wearable. A non-master requester, and a request
/// for a robe the guild does not own, receive 2928 with <see cref="GuildRobeChangedPackets.SelectNotMaster"/> and no
/// 2927. Buying and recolouring a robe happens at the NPC (BuyItemHandler), not here.
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

        var repo = services.GetRequiredService<IGuildRepository>();
        await GuildService.Instance.InitAsync(repo);
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild == null || guild.MasterCharId != player.CharacterId)
        {
            Logger.LogWarning("{Name} asked to switch guild robe {Robe} without being a guild master", player.Name, robeItemId);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
            return;
        }

        byte[] selectionRecord;
        if (robeItemId == GuildRobeRecord.BasicRobeItemId)
        {
            // The Basic Robe is the default; wearing it is always allowed and carries the default colour.
            lock (guild.Gate) { guild.RobeItemId = 0; guild.RobeColorCode = 0; }
            selectionRecord = GuildRobeChangedPackets.BasicRobeSelectedRecord();
            Logger.LogInformation("{Name} switches guild {Guild} to the Basic Robe", player.Name, guild.Name);
        }
        else
        {
            uint colour;
            bool owned;
            lock (guild.Gate)
            {
                owned = guild.OwnedRobes.TryGetValue(robeItemId.ToString(), out colour);
                if (owned) { guild.RobeItemId = robeItemId; guild.RobeColorCode = colour; }
            }
            if (!owned)
            {
                Logger.LogWarning("{Name} asked to wear robe {Robe}, which guild {Guild} has not bought", player.Name, robeItemId, guild.Name);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                    GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
                return;
            }
            selectionRecord = GuildRobeChangedPackets.SelectionRecord(robeItemId, colour, DateTime.Now.ToOADate());
            Logger.LogInformation("{Name} switches guild {Guild} to robe {Robe} (colour {Colour:X8})",
                player.Name, guild.Name, robeItemId, colour);
        }

        await repo.SaveAsync(guild, ct);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
            GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectAccepted), ct);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildRobeChanged,
            GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(selectionRecord), ct);
    }
}
