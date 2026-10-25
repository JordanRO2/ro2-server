using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1506 ReqSkillCastingStart — the client starts casting. The broadcast answer is RMI 2709
/// NfySkillCastingStart, which starts everybody's cast bar for that actor.
/// </summary>
/// <remarks>
/// <para>
/// The request is a fixed 43 bytes:
/// </para>
/// <code>
///   +0  u32 skillId
///   +4  u64 castId          the client's own 64-bit token
///   +12 u8  hasTarget
///   +13 u32 targetHandle
///   +17 f32 x, +21 f32 y, +25 f32 z
///   +29 u32, +33 u32, +37 u32
///   +41 u16 castingVariant
/// </code>
/// <para>
/// <b>The cast id</b> is why this handler echoes rather than generates: the u64 at 2709's blob+16 is
/// matched by the receiving client against its OWN cast ids, and the skill id, the target handle, the
/// float triple (bit for bit) and the u16 variant all agree with the request, so all five are echoed
/// straight through.
/// </para>
/// <para>
/// <b>Recipients.</b> The cast is delivered with a map broadcast with no exclusion. A broadcast with
/// <c>BroadcastToNearbyAsync</c> would EXCLUDE the source session, so the caster's own cast bar would
/// never start.
/// </para>
/// <para>
/// <b>2708 AnsSkillCastingStart is sent as the caster's unicast ack.</b> It is the
/// paired unicast of the request. Its body is a NONE-family fixed 49 bytes (raw, no compact envelope) that
/// echoes the cast
/// request: <c>[u16 result=0][u32 skillId][u64 castId][f32 castSeconds][u8 hasTarget]
/// [u32 targetHandle][f32 x/y/z][u32 0][u32 0][u32 0][u16 0x0101]</c>. Being length-exact and
/// raw, it cannot trip the compact-decoder NULL-blob crash that guessed compact bodies can; the
/// echoed fields are the same ones as in 2709. See
/// <see cref="CombatSkillNotifyPackets.BuildSkillCastingStartAck2708"/>.
/// </para>
/// </remarks>
public class SkillCastingStartHandler : ZoneRmiHandlerBase<SkillCastingStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillCastingStart;
    public override string Name => "SkillCastingStart";

    /// <summary>C2S 1506 body length: 43 bytes.</summary>
    private const int RequestBodyLen = 43;

    /// <summary>Size of the request's hasTarget flag (u8 at +12); the answers re-derive it from the target handle.</summary>
    private const int HasTargetFieldLen = 1;

    /// <summary>Size of the three unused u32 request fields at +29, +33 and +37; they are not echoed.</summary>
    private const int UnusedFieldsLen = 3 * sizeof(uint);

    /// <summary>
    /// Bound a cast's echoed ground point is clamped to, per axis. This is a
    /// generous finite sanity bound, not a map extent (the server holds none); it only stops an absurd finite float
    /// being copied to every client on the map. A non-finite component is refused outright, not clamped.
    /// </summary>
    internal const float MaxCastCoordinate = 1_000_000f;

    /// <summary>Time source for the cast bar's expiry; a test pins it (see <see cref="SkillStartHandler.Clock"/>).</summary>
    internal Func<DateTime> Clock { get; init; } = static () => DateTime.UtcNow;

    public SkillCastingStartHandler(IServiceScopeFactory s, ILogger<SkillCastingStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLen) return;

        uint skillId, targetHandle;
        ulong castId;
        float x, y, z;
        ushort castingVariant;
        {
            var reader = new PacketReader(payload.Span);
            skillId = reader.ReadUInt32();      // +0
            castId = reader.ReadUInt64();       // +4
            reader.Skip(HasTargetFieldLen);      // +12 hasTarget, re-derived by the builder
            targetHandle = reader.ReadUInt32(); // +13
            x = reader.ReadSingle();            // +17
            y = reader.ReadSingle();            // +21
            z = reader.ReadSingle();            // +25
            reader.Skip(UnusedFieldsLen);          // +29/+33/+37
            castingVariant = reader.ReadUInt16(); // +41
        }

        // Everything below is copied to EVERY client on the map (2709) and a malformed S2C can crash a client, so
        // the request is vetted before anything is sent. Refused silently (nothing is echoed, no ack): a legit client
        // never sends any of these.
        //  - the caster must be in the world and alive;
        //  - the ground point must be finite (a NaN / Inf is never forwarded) and is clamped to +-MaxCastCoordinate;
        //  - the skill id must name a Skill_Table_SkillLevel row (the id the 2709 and the cast time resolve against).
        // Not checked here: a 1506 rate cap and a learned-skill check (a 1506 precedes the 1508 that
        // carries the learned / cooldown rules, and item skills have no learned entry).
        var data = services.GetService<GameDataStore>();
        if (!player.MapLoaded || player.IsDead)
        {
            Logger.LogDebug("{Name}: 1506 skill {SkillId} ignored, caster {State}", player.Name, skillId,
                player.IsDead ? "is dead" : "has not loaded the map");
            return;
        }
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            Logger.LogWarning("{Name}: ReqSkillCastingStart for skill {SkillId} with a non-finite position; ignored",
                player.Name, skillId);
            return;
        }
        if (data is null || !data.SkillLevels.TryGetValue(skillId, out var level))
        {
            Logger.LogDebug("{Name}: 1506 skill {SkillId} ignored, no Skill_Table_SkillLevel row", player.Name, skillId);
            return;
        }
        x = Math.Clamp(x, -MaxCastCoordinate, MaxCastCoordinate);
        y = Math.Clamp(y, -MaxCastCoordinate, MaxCastCoordinate);
        z = Math.Clamp(z, -MaxCastCoordinate, MaxCastCoordinate);
        // The cast bar is open from here, so a 1507 cancel of it has something to cancel (SkillCastingCancelHandler).
        SkillCastLandings.BeginCasting(player, skillId, Clock());

        // A skill cast is a combat action: enter / keep the player's battle stance (PcCombatStateService).
        services.GetService<PcCombatStateService>()?.MarkCombat(player);

        // The cast time is the table value (Skill_Table_SkillLevel.Casting_Time), its upper bound; no
        // haste model is applied.
        // Skill_Table_SkillLevel is keyed by its own ID column, and the wire's skill id is that ID,
        // which is what 2709's blob+12 resolves against.
        var castSeconds = level.Casting_Time;

        var notify = SkillDamagePackets.BuildSkillCastingStart2709(
            casterHandle: player.EntityId,
            skillId: skillId,
            castId: castId,
            castSeconds: castSeconds,
            targetHandle: targetHandle,
            x: x, y: y, z: z,
            castingVariant: castingVariant);

        // Starting a cast ends the ride first: a mounted rider's 1506 is followed by
        // its own drop (2497 2498 2493 2494 2712), ahead of the 2708. A riding skill keeps it
        // (RidingService.KeepsRide).
        if (!RidingService.KeepsRide(data, skillId))
            await RidingService.DropAsync(session, player, services);

        // No exclusion: the caster must receive its own cast bar. See the class remarks.
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfySkillCastingStart,
            writer => writer.WriteBytes(notify));

        // Paired unicast ack to the caster (2708 AnsSkillCastingStart). Additive: the broadcast above
        // is unchanged. NONE-family fixed 49 bytes, echoing the same cast fields. result = 0 (success).
        var ack = CombatSkillNotifyPackets.BuildSkillCastingStartAck2708(
            skillId: skillId,
            castId: castId,
            castSeconds: castSeconds,
            targetHandle: targetHandle,
            x: x, y: y, z: z);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSkillCastingStart,
            writer => writer.WriteBytes(ack));
    }
}
