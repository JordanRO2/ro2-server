using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1511 ReqGuildSetBribePercent: sets the member's guild contribution ("bribe") percentage, as one byte.</summary>
/// <remarks>
/// <para>
/// The answer is 2721 AnsGuildSetBribePercent: [u16 result 0][u8 requested percent].
/// </para>
/// <para>
/// The percentage is stored on the member (<see cref="GuildMember.BribePercent"/>) and persists across sessions. It is capped at <see cref="GuildService.MaxBribePercent"/>, so the echoed value equals the stored value. The percentage sets the share of quest prize experience that goes to the guild instead of the player (see <see cref="GuildBribePackets.HuntingBribeAmount"/>), delivered with the 2725 NfyGuildBribeHunting notice.
/// </para>
/// <para>
/// The 2726 NfyGuildBribePCLevelUp notice is sent by <c>CombatService.GrantExpAsync</c> once per level-up of a guilded player (see <see cref="GuildBribePackets"/>).
/// </para>
/// </remarks>
public class GuildSetBribePercentHandler : ZoneRmiHandlerBase<GuildSetBribePercentHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSetBribePercent;
    public override string Name => "GuildSetBribePercent";
    public GuildSetBribePercentHandler(IServiceScopeFactory s, ILogger<GuildSetBribePercentHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 1) return;
        var requested = payload.Span[0];

        
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        
        
        byte answered = requested;
        if (GuildService.Instance.GetByCharId(player.CharacterId) is { } guild
            && await GuildService.Instance.SetBribePercentAsync(guild, player.CharacterId, requested) is { } stored)
        {
            answered = stored;
        }

        Logger.LogDebug("{Name} sets guild bribe percent to {Pct} (requested {Req})", player.Name, answered, requested);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSetBribePercent, writer =>
        {
            writer.WriteUInt16(0);      
            writer.WriteByte(answered); 
        });
    }
}
