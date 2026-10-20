using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1507 ReqSkillCastingCancel - Cancel casting</summary>
/// <remarks>
/// <para>
/// REQUEST: a fixed <c>[u32 skillId]</c> (read_side <c>Blob&lt;4&gt;</c>).
/// </para>
/// <para>
/// ANSWER: every 1507 is answered with <c>2710 AnsSkillCastingCancel
/// [u16 0][u32 skillId]</c>.
/// </para>
/// <para>
/// The next S2C frame after each answer is a <c>2711 NfySkillCastingCancel</c> for
/// the same skill id whose caster handle is the requesting player's own. Other players' casts are
/// announced to this client with the same message, carrying their own handle (tag 0x13 for an NPC, tag 0x11
/// for another PC, the onlooker copy this handler broadcasts).
/// </para>
/// <para>
/// A handler that sent no 2710 and broadcast 2711 with <c>BroadcastToNearbyAsync</c> (which
/// skips the sender) would leave the one client that asked for the cancel without either answer.
/// </para>
/// </remarks>
public class SkillCastingCancelHandler : ZoneRmiHandlerBase<SkillCastingCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillCastingCancel;
    public override string Name => "SkillCastingCancel";
    /// <summary>Time source for the active-cast test; a test pins it (see <see cref="SkillStartHandler.Clock"/>).</summary>
    internal Func<DateTime> Clock { get; init; } = static () => DateTime.UtcNow;

    /// <summary>Size of the 1507 request body: a single u32 skill id.</summary>
    private const int RequestLength = sizeof(uint);

    public SkillCastingCancelHandler(IServiceScopeFactory s, ILogger<SkillCastingCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // A 1507 is exactly 4 bytes; a shorter body has no skill id to echo, and answering it
        // with 0 would tell the client to cancel a skill that does not exist.
        if (payload.Length < RequestLength)
        {
            Logger.LogWarning("{Name}: ReqSkillCastingCancel of {Len} bytes, expected {Expected}", player.Name, payload.Length, RequestLength);
            return;
        }
        uint skillId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        Logger.LogDebug("{Name} cancels casting skill {SkillId}", player.Name, skillId);

        // 1) 2710 to the caster: result 0 + the request's skill id.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSkillCastingCancel,
            writer => writer.WriteBytes(CombatSkillNotifyPackets.BuildSkillCastingCancelAck2710(skillId)), ct);

        // The 2711 that goes to everyone else is gated on the caster really having that cast open (a 1506 casting
        // start, or a 1508 / 1561 the server accepted: SkillCastLandings.IsCasting). Without it any client could make every
        // client nearby abort the named skill's cast bar with a made-up id. The caster's own ack (2710) and own 2711 stay
        // unconditional: they name only the caster itself. A 1507 rate cap is not implemented.
        var now = Clock();
        var active = SkillCastLandings.IsCasting(player, skillId, now);
        SkillCastLandings.EndCasting(player, skillId);

        // 2) 2711 to the caster as well (right after the 2710), then
        // to everyone else nearby. 2711 is compact-BA, msgTag 58441 (0xE449), body 18 =
        // [u32 0][u32 casterHandle][u32 skillId], starting
        // 01 10 10 00 49 e4, the same shape as the 2585/2586/2747 builders.
        var body = ActorStatePackets.BuildNfySkillCastingCancel(player.EntityId, skillId);
        await session.SendRmiAsync(ZoneS2COpcodes.NfySkillCastingCancel, writer => writer.WriteBytes(body), ct);
        if (!active)
        {
            Logger.LogDebug("{Name}: 1507 cancel of skill {SkillId} has no open cast; not broadcast", player.Name, skillId);
            return;
        }
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySkillCastingCancel,
            writer => writer.WriteBytes(body));
    }
}
