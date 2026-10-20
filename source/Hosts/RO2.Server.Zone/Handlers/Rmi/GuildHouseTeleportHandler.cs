using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1689 ReqGuildHouseTeleport: the guild-house button of the guild window asks to be moved to the
/// guild's house. Empty body.
/// </summary>
/// <remarks>
/// <para>
/// Sender: UpdateGuildMemberListDisplay calls a game-object slot, which is a thunk.
/// That thunk calls the Zone_C2S proxy with no fields, so the body is empty. No retail frame of 1689
/// or of its answer is known.
/// </para>
/// <para>
/// 3022 AnsGuildHouseTeleport is a bare <c>[u16 result]</c>: read_side U16, fixed 2 bytes, no envelope.
/// Its consumer AnsGuildHouseTeleport_Handler_RMI3022 handles only four values:
/// <list type="bullet">
/// <item>0 returns 1 and shows nothing. The move itself would be a server map change.</item>
/// <item>25210 (0x627A) opens a message box with string_ui 2548 "Can't move to this place."</item>
/// <item>25211 (0x627B) shows 7709 "Cannot use during battle."</item>
/// <item>25212 (0x627C) shows 10885 "Unable to use in this map".</item>
/// </list>
/// Every other value returns 0 with no message. The old reply, 1, was one of those, so the client dropped
/// it silently.
/// </para>
/// <para>
/// This server has no guild houses: no lottery, no ownership and no house instance (see
/// GuildHouseTicketHandler, GuildHouseLotteryHandler, GuildHouseResultHandler), so there is nowhere to move
/// the player to. The answer is the consumer's generic refusal, 25210. Nothing is teleported.
/// </para>
/// </remarks>
public class GuildHouseTeleportHandler : ZoneRmiHandlerBase<GuildHouseTeleportHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildHouseTeleport;
    public override string Name => "GuildHouseTeleport";
    public GuildHouseTeleportHandler(IServiceScopeFactory s, ILogger<GuildHouseTeleportHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>3022 result 25210: the client shows string_ui 2548 "Can't move to this place.".</summary>
    internal const ushort ResultCannotMoveHere = 0x627A;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} asks to teleport to the guild house; no guild house exists, refused with {Code}",
            player.Name, ResultCannotMoveHere);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildHouseTeleport,
            writer => writer.WriteUInt16(ResultCannotMoveHere), ct);
    }
}
