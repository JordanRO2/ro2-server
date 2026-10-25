using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1524 ReqSkillMoveCancel (Fixed 5B) - Cancel movement skill</summary>
/// <remarks>
/// <para>
/// Request, raw 5 bytes <c>[u32 skillId][u8 isCurrentCast]</c>. The client sets the byte to 1 when the
/// skill cancelled is the one the player is casting now, and to 0 otherwise.
/// </para>
/// <para>
/// The answer is 2746 AnsSkillMoveCancel to the caster (6 bytes, result 0 and the
/// skill echoed), then, in the same millisecond, 2747 NfySkillMoveCancel naming the
/// caster to the caster itself and to the players around it. The caster's own copy is required
/// (BroadcastToNearbyAsync skips the source session): the requester's stream holds a 2747 with its own handle
/// (its 1526 handle) and the same skill, while 2747s that name other PCs are onlooker copies.
/// </para>
/// <para>
/// The flag does not change the audience: a flag-0 answer carries the same self copy.
/// </para>
/// <para>
/// What the self copy does on the client: it looks up the actor at
/// blob+8 and, when that is the local player, ends the move-skill cast on it; without it the caster's own
/// client is never told to leave the move-skill cast. Onlookers end the cast on the
/// caster's actor. This server answers every 1524.
/// </para>
/// </remarks>
public class SkillMoveCancelHandler : ZoneRmiHandlerBase<SkillMoveCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillMoveCancel;
    public override string Name => "SkillMoveCancel";

    /// <summary>Size of the 1524 request: <c>[u32 skillId][u8 isCurrentCast]</c>.</summary>
    private const int RequestSize = 5;

    /// <summary>Result code of 2746 AnsSkillMoveCancel on success.</summary>
    private const ushort ResultSuccess = 0;

    public SkillMoveCancelHandler(IServiceScopeFactory s, ILogger<SkillMoveCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Fixed 5B: [uint32 skillId][byte isCurrentCast]
        if (payload.Length < RequestSize) return;
        var reader = new PacketReader(payload.Span);
        var skillId = reader.ReadUInt32();
        var isCurrentCast = reader.ReadByte();

        Logger.LogDebug("{Name} cancels skill move {SkillId} isCurrentCast={Flag}", player.Name, skillId, isCurrentCast);

        // Send AnsSkillMoveCancel (2746) ack. The body is a fixed 6B record (Blob<6>):
        // [u16 result][u32 skillId], with result 0 and the skillId echoed from 1524.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSkillMoveCancel, writer =>
        {
            writer.WriteUInt16(ResultSuccess); // result = success
            writer.WriteUInt32(skillId); // echoed from the C2S 1524 request
        });

        // NfySkillMoveCancel (2747), compact-BA (msgTag 58464): [u32 0][u32 casterHandle][u32 skillId], the
        // handle from state and the skill from the 1524 (ActorStatePackets.BuildNfySkillMoveCancel). First the
        // caster's own copy, right after its 2746, then the
        // players around it; see the remarks for why the isCurrentCast byte does not change this.
        var body = ActorStatePackets.BuildNfySkillMoveCancel(player.EntityId, skillId);
        await session.SendRmiAsync(ZoneS2COpcodes.NfySkillMoveCancel, body);

        // The nearby copy tells every onlooker "this caster's skill ended", with the client's skillId. Only a
        // skill the server has the caster casting (open cast bar or pending 1508/1561) may say that; otherwise any
        // client could forge a cancel of an arbitrary skill on the whole map. The 2746 ack and the caster's own copy
        // above are unicast and stay.
        if (!SkillCastLandings.IsCasting(player, skillId, DateTime.UtcNow))
        {
            Logger.LogDebug("{Name}: skill move cancel {SkillId} is not a skill it is casting; 2747 not broadcast", player.Name, skillId);
            return;
        }
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySkillMoveCancel,
            writer => writer.WriteBytes(body));
    }
}
