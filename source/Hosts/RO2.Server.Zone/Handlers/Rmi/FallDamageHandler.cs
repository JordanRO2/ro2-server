using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1565 ReqFallDamage. The client reports a fall and the server answers with 2815 NfyFallDamage.
/// </summary>
/// <remarks>
/// <para>
/// The body is a single f32, the fall magnitude reported by the client. It is NOT the damage: the server
/// turns it into damage with the rule in <see cref="FallDamagePackets.RetailFallDamage"/>.
/// </para>
/// <para>
/// The reply is 2815 to the faller AND to every PC that can see them. 2401 OnActorAutoHeal is not sent after
/// a 1565: it has no damage number.
/// </para>
/// <para>
/// Reachable: 1565 is only sent while the PC is in the world, so the PlayerState that
/// <see cref="ZoneRmiHandlerBase{THandler}"/> requires already exists.
/// </para>
/// <para>
/// <b>Why the damage is applied.</b> The magnitude must be read as an f32 (reading it as an int32 turns
/// every report into a huge damage). Falls are charged right after a map load too, so no grace period is
/// modelled.
/// </para>
/// </remarks>
public class FallDamageHandler : ZoneRmiHandlerBase<FallDamageHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFallDamage;
    public override string Name => "FallDamage";

    /// <summary>blob+19 of the state-0 2398 that opens a fall death: 2 for the field-map form
    /// (see HandleAsync).</summary>
    private const uint FallDeathNormalStateTrailer = 2;

    /// <summary>Length of the 1565 body: one f32 fall magnitude.</summary>
    private const int FallMagnitudeSize = 4;

    /// <summary>Killer handle of the 2392 OnDeadNotify for a fall death: 0 (no killer).</summary>
    private const uint NoKillerHandle = 0;

    public FallDamageHandler(IServiceScopeFactory s, ILogger<FallDamageHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < FallMagnitudeSize) return;
        var reader = new PacketReader(payload.Span);
        var reported = reader.ReadSingle(); // f32. Reading it as int32 interprets the IEEE-754 bits.

        // A NaN / Inf / negative magnitude is no fall (a forged +Inf was a one-packet self-kill): ignored, nothing
        // sent. A finite one is clamped to the defensive ceiling before the rule. The server has no authoritative Z
        // delta to compare it with, so this is the whole guard (see FallDamagePackets.MaxFallMagnitude).
        if (!FallDamagePackets.TryGuardMagnitude(reported, out var fallMagnitude))
        {
            Logger.LogWarning("{Name}: ignoring ReqFallDamage with magnitude {V}", player.Name, reported);
            return;
        }

        // A corpse takes no further damage: the fall that killed it was the last one charged.
        bool alreadyDead = player.IsDead || player.Hp <= 0;
        int damage = alreadyDead ? 0 : FallDamagePackets.RetailFallDamage(fallMagnitude, player.MaxHp);
        var (_, lethal) = FallDamagePackets.ApplyFallDamage(player.Hp, damage);

        if (lethal)
        {
            // Same dead state NpcCombatService sets on a lethal hit, set atomically before anything is sent so no
            // other pass sees a 0-HP player as alive: SP goes to 0 with HP (the 2391 head+85 is 0 on death),
            // RegenService skips a dead player, and RevivalHandler restores both.
            player.SetVitalsDead();
            AutoAttackState.Disengage(player);

            // A fall death opens with two frames before the first state-6 2398:
            // 2938 AnsSiegeArmsUseEnd 01 00 to the faller only, then a state-0 2398 for the faller to the map. The
            // 2938 is the faller's alone; onlookers get only the s0. The s0's blob+15/+19 are (0, 2) for the field-map
            // form; the PK/siege-map form, (1, 1001..1007), is not modelled here either for the s6
            // (PlayerDeathPackets.DeadStateTrailerFieldMap). BuildChangeState2398(pc, 0, 0, 2) builds the (0, 2) frame.
            // A non-lethal fall gets neither: 2815 then 2401 only.
            // The Illusion Pyramid close 29012 goes out directly before that 2938 when the pyramid is armed
            // (IllusionPyramidService, DEATH).
            await IllusionPyramidService.CloseOnDeathAsync(session, player, services, Logger, ct);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd,
                writer => writer.WriteBytes(SessionExitPackets.Ack2938));
            var normalState = CombatPackets.BuildChangeState2398(player.EntityId, CombatPackets.ActorState.PcNormal,
                targetHandle: 0, trailing: FallDeathNormalStateTrailer);
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeState,
                w => w.WriteBytes(normalState));

            // A death fails the "without dying" Kharas of the faller and its party: the 2581s go right before
            // the victim's first state-6 2398, as on a monster kill (see KharaFailPackets), and after the
            // s0. The death-wear burst goes between the s0 and the Khara frames, slots 1..12 ascending, persisted
            // before the 2391 reads the gear (DurabilityService.ApplyDeathWearAsync). Then the Complete_Type 26
            // death count of the faller's Kharas: that 2574/2577 (Khara 62) sits right after the burst.
            await services.GetRequiredService<DurabilityService>().ApplyDeathWearAsync(player, ct);
            await KharaService.OnDeathAsync(session, player, services, Logger, ct);
            await KharaFailPackets.SendDeathFailuresAsync(player, services, Broadcast, Logger, ct);

            // The faller's state-6 2398 is sent BEFORE the 2815.
            // Map broadcast: the faller gets it too.
            var deadState = PlayerDeathPackets.BuildOnChangeStateDead2398(player.EntityId);
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeState,
                w => w.WriteBytes(deadState));
        }
        else
        {
            // Non-lethal: subtract the fall as a delta under the vitals lock, so a concurrent lethal combat/DoT
            // write is not overwritten and a player killed mid-handler is not revived (TryApplyHpDelta refuses a
            // dead player). A damage of 0 (safe fall, or a corpse) is a no-op, as the old hpAfter assignment was.
            player.TryApplyHpDelta(-damage, out _);
        }

        Logger.LogDebug("{Name} fell (v={V:F3}): damage {Dmg}, HP now {Hp}{Dead}",
            player.Name, fallMagnitude, damage, player.Hp, lethal ? " (dead)" : "");

        var body = FallDamagePackets.BuildFallDamage2815(player.EntityId, player.Hp, damage);

        // Same bytes to both audiences. Watchers need the HP to update the faller's bar and the
        // floating number. BroadcastToNearbyAsync skips the source session, so the faller gets
        // their own copy here.
        await session.SendRmiAsync(ZoneS2COpcodes.NfyFallDamage, writer => writer.WriteBytes(body));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyFallDamage, writer => writer.WriteBytes(body));

        if (lethal)
            await SendFallDeathAsync(session, player, damage, services, ct);
    }

    /// <summary>
    /// The rest of a fall death, after the 2815: 2391 + 2836 + 2837 to the faller, the removal of its
    /// After_Death_Handling 0 effects (<see cref="DeathEffects"/>), the state-6 2398 again and the 2392
    /// OnDeadNotify with no killer, to the map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a fall death the faller's own session gets 2391 + 2836 + 2837 right after the 2815, a second
    /// state-6 2398 follows, and then a 2392 for the faller shortly after the 2815, whose killer is 0 and whose
    /// blob+16 is this 2815's damage.
    /// Sent at once here, as NpcCombatService sends the 2392 of a monster kill.
    /// </para>
    /// <para>
    /// The 2938 and the state-0 2398 that open the death, and the 2581 NfyKharaFailed burst when the faller
    /// or a party member holds a Fail_Type-49 Khara, went out before the first state-6 2398 (HandleAsync), and
    /// so did the death-wear 2503 burst sent between the s0 and the s6 (DurabilityService.ApplyDeathWearAsync)
    /// and the Illusion Pyramid's 29012 close ahead of the 2938. The faller also stays on the monsters' threat tables, which only
    /// NpcCombatService can edit; its monsters skip a dead victim, so they stop hitting it.
    /// </para>
    /// </remarks>
    private async Task SendFallDeathAsync(ProudSession session, PlayerState player, int damage,
        IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var gameData = services.GetRequiredService<GameDataStore>();
            if (!await PlayerDeathPackets.SendDeadMeAsync(session, player, inventory, gameData, ct))
                Logger.LogWarning("{Name} died from a fall but the worn-gear read failed; 2391 sent with no records",
                    player.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: sending 2391 OnDeadMe after a fall failed", session.SessionId);
        }

        // The death ends the faller's After_Death_Handling 0 effects right after the 2391 + 2836 + 2837 and before the
        // second state-6 2398 and the 2392, as on a monster kill (see DeathEffects).
        try
        {
            await DeathEffects.DropDeathEffectsAsync(session, player, services.GetRequiredService<GameDataStore>(), services);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: removing the effects a fall death ends failed", session.SessionId);
        }

        var deadState = PlayerDeathPackets.BuildOnChangeStateDead2398(player.EntityId);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeState,
            w => w.WriteBytes(deadState));

        var deadNotify = ActorVitalsPackets.BuildDeadNotify2392(player.EntityId, killerHandle: NoKillerHandle,
            lethalDamage: (uint)Math.Max(0, damage));
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnDeadNotify,
            w => w.WriteBytes(deadNotify));

        Logger.LogInformation("{Name} died from a fall ({Damage} damage) on map {Map}",
            player.Name, damage, player.MapId);
    }
}
