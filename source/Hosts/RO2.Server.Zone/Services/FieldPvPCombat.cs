using Microsoft.Extensions.DependencyInjection;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The fight of a started field duel: who may hit whom, what a hit does to the victim's HP, and the fought-out
/// end (2736 code 3) that a hit which would take a duelist to 0 HP triggers instead of a death, whether the hit
/// is the opponent's or a monster's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who.</b> Only the two duelists of a duel whose 2758 went out (<see cref="FieldPvPService.Duel.Started"/>),
/// both alive and on the duel's map, and only each other. The client arms the pair at that 2758 (it writes the
/// duel id and sides into both actors), and a PC's normal attack on a PC is granted and answered like one on a
/// monster.
/// </para>
/// <para>
/// <b>The fought-out end.</b> The sequence, in order:
/// <list type="number">
/// <item>The hit that would take the loser to 0 leaves it at HP 1: the 2401 after the result carries curHP 1, and no
/// 2391/2392 death follows.</item>
/// <item>In the same millisecond, BEFORE the 2736, the harmful effects on the two are removed through the normal
/// removal run (2498/2497/2493/2494). Harmful effects are those with Effect_Table Influence_Type 2 (knockdown,
/// stun, slow, overdose, ...); the loser's come first.</item>
/// <item>2736 <c>[self][winner][loser][u16 3]</c> to both duelists.</item>
/// <item>The end 2398 pair about both (<see cref="FieldPvPStatePackets"/>), the winner's first.</item>
/// <item>The loser's 2401 OnActorAutoHeal (curHP 1, its SP and maxima), the third frame after the 2736. It is a
/// map broadcast, so observers of the duel see the HP-1 value too.</item>
/// <item>Only then the lethal damage message itself, with the full damage and victim HP 1, within a few
/// milliseconds after the 2736 (2797, 2718, 2696 or 2495).</item>
/// </list>
/// </para>
/// <para>
/// <b>A monster's hit ends it the same way.</b> The hit need not be the opponent's: when a monster's normal hit
/// would take a duelist of a started duel to 0, that duelist is also left at HP 1 and the duel ends with the
/// OTHER duelist as winner, instead of running a death. The sequence is: the monster's 2694 on the loser, a few
/// milliseconds before the effect removals; the end above unchanged (removals, 2736, end pair, HP-1 2401); the
/// monster's 2696, damage kind 1, the hit's damage and victim HP 1, its SP the 2401's (SP is not zeroed as in a
/// death); in the loser's own session its armour 2503 burst; and last one more end-shape 2398 about the loser (the
/// opponent's 2696 ends do not carry it). No 2581, 2391, 2392 or 2630 follows: nobody died.
/// <see cref="TryFightOutMonsterHitAsync"/> is that path, and NpcCombatService's retaliation pass hands it every
/// hit that would take a player to 0 before it runs a death.
/// </para>
/// <para>
/// OPEN: no data table carries a PvP damage modifier, so whether PvP damage is scaled is not established and the
/// PvE formulas are used (CombatService.ResolvePvpHit). DoT-lethal ends (2495) cannot happen here, because this
/// server applies no skill effects to PCs; and no path of this server puts an Influence_Type 2 effect on a PC
/// today (no ItemInfo row's Effect_ID_1 is one), so step 2 finds nothing to remove until one does. It is kept
/// because it is part of the end sequence, not tied to any particular effect.
/// </para>
/// </remarks>
public static class FieldPvPCombat
{
    /// <summary>Effect_Table.Influence_Type of a harmful effect: what is removed at a fought-out end.</summary>
    internal const byte HarmfulInfluenceType = 2;

    /// <summary>The HP a fought-out loser is left at.</summary>
    internal const int FoughtOutHp = 1;

 /// <summary>Mask of the actor type tag, the low byte of an actor handle (a PC carries <see cref="PlayerStateManager.PcActorTag"/>).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>The duelist a started duel pairs the attacker with, and that duel.</summary>
    public readonly record struct Opponent(PlayerState Victim, FieldPvPService.Duel Duel);

    /// <summary>
    /// The victim <paramref name="handle"/> names when it is <paramref name="attacker"/>'s opponent in a started
    /// duel: a PC handle, the other duelist of the attacker's started duel, the same character on the duel's map,
    /// and both of them alive. Null otherwise.
    /// </summary>
    public static Opponent? TryGetStartedOpponent(PlayerState attacker, uint handle, PlayerStateManager players)
    {
        if ((handle & ActorTagMask) != PlayerStateManager.PcActorTag || handle == attacker.EntityId)
            return null;
        if (attacker.IsDead || attacker.Hp <= 0)
            return null;
        if (FieldPvPService.StartedDuelOf(attacker.EntityId, players) is not { } duel)
            return null;

        var (otherHandle, otherSession) = duel.Challenger == attacker.EntityId
            ? (duel.Target, duel.TargetSession)
            : (duel.Challenger, duel.ChallengerSession);
        if (otherHandle != handle)
            return null;
        if (players.GetPlayer(otherSession) is not { } victim || victim.EntityId != handle
            || victim.MapId != duel.MapId || attacker.MapId != duel.MapId
            || victim.IsDead || victim.Hp <= 0)
            return null;
        return new Opponent(victim, duel);
    }

    /// <summary>
    /// The started-duel opponents among <paramref name="handles"/> (a skill's recorded hit set), keyed by handle.
    /// A duelist has one opponent, so this holds at most one entry.
    /// </summary>
    public static Dictionary<uint, Opponent> OpponentsIn(IReadOnlyList<uint> handles, PlayerState attacker,
        PlayerStateManager players)
    {
        var found = new Dictionary<uint, Opponent>();
        foreach (var handle in handles)
        {
            if (!found.ContainsKey(handle) && TryGetStartedOpponent(attacker, handle, players) is { } opponent)
                found[handle] = opponent;
        }
        return found;
    }

    /// <summary>
    /// Takes <paramref name="damage"/> off <paramref name="victim"/>'s HP. A hit that would take it to 0 or below
    /// leaves it at <see cref="FoughtOutHp"/> instead and returns true: the duel is then fought out, and the
    /// caller must run <see cref="EndFoughtOutAsync"/> BEFORE it sends the hit's damage message.
    /// </summary>
    public static bool TakeHit(PlayerState victim, int damage)
    {
        lock (victim.Vitals)
        {
            if (victim.Hp - damage <= 0)
            {
                victim.Hp = FoughtOutHp;
                return true;
            }
            victim.ApplyHpDelta(-damage);
            return false;
        }
    }

    /// <summary>
    /// The Effect_Table ids of <paramref name="player"/>'s active effects whose Influence_Type is 2 (harmful), in
    /// the order they were applied.
    /// </summary>
    internal static List<uint> HarmfulEffectsOf(PlayerState player, IReadOnlyList<EffectTable> effectTable)
    {
        uint[] active;
        lock (player.ActiveEffects)
            active = player.ActiveEffects.Select(e => e.EffectId).Distinct().ToArray();
        if (active.Length == 0)
            return new List<uint>();

        var harmful = new HashSet<uint>();
        foreach (var row in effectTable)
        {
            if (row.Influence_Type == HarmfulInfluenceType)
                harmful.Add(row.ID);
        }
        return active.Where(harmful.Contains).ToList();
    }

    /// <summary>
    /// Ends <paramref name="duel"/> as fought out, in retail's order (see the remarks): the harmful effects of the
    /// loser, then of the winner, removed; 2736 code 3 to both duelists; the end 2398 pair to the map, the
    /// winner's first; the loser's HP-1 2401 to the map. The caller sends the lethal hit's own damage message
    /// right after. False, sending nothing, when the duel was already closed by another path.
    /// </summary>
    public static async Task<bool> EndFoughtOutAsync(FieldPvPService.Duel duel, PlayerState winner, PlayerState loser,
        IServiceProvider services, BroadcastService broadcast, CancellationToken ct)
    {
        if (!FieldPvPService.TryClose(duel.Id, out _))
            return false;
        await SendFoughtOutEndAsync(duel.MapId, winner, loser, services, broadcast, ct);
        return true;
    }

    /// <summary>
    /// Lands a monster's normal hit of <paramref name="damage"/> on <paramref name="victim"/> when that hit would
    /// take it to 0 HP while it fights a started duel: retail then leaves it at HP 1 and ends the duel as fought
    /// out, the OTHER duelist the winner, instead of killing it (see the remarks). This sends the whole hit in
    /// retail's order — the monster's 2694, the fought-out end (<see cref="EndFoughtOutAsync"/>'s frames), the
    /// monster's 2696 with the loser at HP 1 and its SP kept, the loser's armour wear, and one more end-shape 2398
    /// about the loser — and returns true: the caller then sends nothing more for this hit and runs no death.
    /// </summary>
    /// <returns>
    /// False, having sent nothing and changed nothing, when the hit would not take the victim to 0, the victim is
    /// in no started duel, its opponent is dead, or another path closed the duel first. The caller then lands the
    /// hit as it would on any player, death included.
    /// </returns>
    /// <remarks>
    /// Called by NpcCombatService's retaliation pass, which runs outside any request: <paramref name="services"/>
    /// is a scope it creates, and only singletons are read from it (EffectService and GameDataStore for the end,
    /// DurabilityService for the wear). The 2694, 2696 and 2503 are built exactly as that pass builds them for any
    /// other monster hit (CombatPackets, DurabilityService.ApplyArmourWearOnTakenHitAsync). The duel is claimed
    /// (<see cref="FieldPvPService.TryClose"/>) before any HP changes or anything is sent, so a concurrent end from
    /// the duelists' own hits or a forfeit cannot make both paths end it, or leave this hit half sent.
    /// </remarks>
    public static async Task<bool> TryFightOutMonsterHitAsync(NpcRuntimeState npc, PlayerState victim, int damage,
        byte damageKind, PlayerStateManager players, IServiceProvider services, BroadcastService broadcast,
        CancellationToken ct)
    {
        if (victim.IsDead || victim.Hp <= 0 || victim.Hp - damage > 0)
            return false;
        if (FieldPvPService.StartedDuelOf(victim.EntityId, players) is not { } duel)
            return false;

        var (winnerHandle, winnerSession) = duel.Challenger == victim.EntityId
            ? (duel.Target, duel.TargetSession)
            : (duel.Challenger, duel.ChallengerSession);
        if (players.GetPlayer(winnerSession) is not { } winner || winner.EntityId != winnerHandle
            || winner.IsDead || winner.Hp <= 0)
            return false;
        if (!FieldPvPService.TryClose(duel.Id, out _))
            return false;

        lock (victim.Vitals)
            victim.Hp = FoughtOutHp;

        // The monster's swing first: a few milliseconds before the end's first removal.
        await broadcast.BroadcastToMapAsync(duel.MapId, ZoneS2COpcodes.NfyNormalAttackStart,
            w => w.WriteBytes(CombatPackets.BuildAttackStart2694(npc.EntityOid, victim.EntityId)));

        await SendFoughtOutEndAsync(duel.MapId, winner, victim, services, broadcast, ct);

        // Then the hit itself, the loser at HP 1 with its SP as the 2401 just reported it.
        var damageBody = CombatPackets.BuildAttackDamage2696(
            attackerHandle: npc.EntityOid,
            attackerCurHp: (uint)Math.Max(0, npc.Hp),
            attackerCurSp: 0,
            victimHandle: victim.EntityId,
            victimCurHpAfter: (uint)victim.Hp,
            victimCurSp: (ushort)Math.Clamp(victim.Mp, 0, ushort.MaxValue),
            damage: (uint)Math.Max(0, damage),
            damageKind: damageKind);
        await broadcast.BroadcastToMapAsync(duel.MapId, ZoneS2COpcodes.NfyAttackDamage,
            w => w.WriteBytes(damageBody));

        // A damaging hit taken wears the victim's armour whatever it ends (the 2503 burst after the 2696 in the
        // loser's session), then the last end-shape 2398 about the loser.
        await services.GetRequiredService<DurabilityService>().ApplyArmourWearOnTakenHitAsync(victim, ct);
        var loserState = FieldPvPStatePackets.BuildEnd2398(victim.EntityId);
        await broadcast.BroadcastToMapAsync(duel.MapId, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(loserState));
        return true;
    }

    /// <summary>
    /// The frames of a fought-out end once the duel is closed: the harmful effects of the loser, then of the
    /// winner, removed; 2736 code 3 to both duelists; the end 2398 pair to the map, the winner's first; the
    /// loser's HP-1 2401 to the map.
    /// </summary>
    private static async Task SendFoughtOutEndAsync(uint mapId, PlayerState winner, PlayerState loser,
        IServiceProvider services, BroadcastService broadcast, CancellationToken ct)
    {
        var effects = services.GetRequiredService<EffectService>();
        var table = services.GetRequiredService<GameDataStore>().EffectTable;
        foreach (var duelist in new[] { loser, winner })
        {
            foreach (var effectId in HarmfulEffectsOf(duelist, table))
                await effects.RemovePlayerEffectAsync(duelist, effectId);
        }

        foreach (var self in new[] { winner, loser })
        {
            var session = broadcast.GetSession(self.SessionId);
            if (session is { IsConnected: true })
                await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyResultFiledPvP,
                    FieldPvPPackets.BuildResult2736(self.EntityId, winner.EntityId, loser.EntityId,
                        FieldPvPPackets.ResultFoughtOut), ct);
        }

        await FieldPvPStatePackets.BroadcastEndPairAsync(broadcast, mapId, winner.EntityId, loser.EntityId);

        var vitals = ActorVitalsPackets.BuildActorAutoHeal2401(loser.EntityId, loser.Hp, loser.Mp,
            loser.MaxHp, loser.MaxMp);
        await broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnActorAutoHeal, w => w.WriteBytes(vitals));
    }
}
