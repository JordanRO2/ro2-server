using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the actor vitals notifies, kept in one place so they cannot drift apart.
/// </summary>
/// <remarks>
/// Every message that puts HP/SP on the wire is built here rather than inline at the call site, so duplicate
/// builders cannot drift apart.
/// </remarks>
public static class ActorVitalsPackets
{
    /// <summary>Value of every reserved / constant-zero slot in these bodies.</summary>
    private const uint ReservedZero = 0;

    /// <summary>msgTag of 2401 OnActorAutoHeal.</summary>
    internal const ushort MsgTagActorAutoHeal = 58091; // 0xE2EB

    /// <summary>Body length of 2401 in bytes.</summary>
    internal const int AutoHealBodyLen = 30;

    /// <summary>
    /// Builds RMI 2401 OnActorAutoHeal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wire layout:
    /// <code>
    ///   [compactLen 01 1C][u16 selfLen=28][u16 msgTag=58091]
    ///   [u32 actorHandle]      blob+4
    ///   [u32 actorHandle]      blob+8, the same handle again for a PC
    ///   [u32 curHp]            blob+12
    ///   [u32 curSp]            blob+16
    ///   [u32 maxHp]            blob+20
    ///   [u32 maxSp]            blob+24
    /// </code>
    /// 30 bytes including the envelope.
    /// </para>
    /// <para>
    /// The field order is (cur, cur, max, max), not (cur, max, cur, max): current HP and SP never exceed the
    /// maxima under this reading.
    /// </para>
    /// <para>
    /// The actor handle is the id the client uses to find the actor, not a session number. The body must be
    /// wrapped in the compact-length envelope: the client's compact-length decoder accepts only marker bytes
    /// {1,2,4,8}, and a rejected body is passed on as NULL to a handler that dereferences it, so a raw body here
    /// crashes the client on the first regeneration tick after damage.
    /// </para>
    /// </remarks>
    public static byte[] BuildActorAutoHeal2401(uint actorHandle, int curHp, int curSp,
        int maxHp, int maxSp, bool isPc = true)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagActorAutoHeal);   // blob+2
            // blob+4 mirrors the handle for a PC, but is zero for an NPC. blob+8 is always the subject.
            p.WriteUInt32(isPc ? actorHandle : ReservedZero);
            p.WriteUInt32(actorHandle);
            p.WriteUInt32((uint)Math.Max(0, curHp));   // blob+12
            p.WriteUInt32((uint)Math.Max(0, curSp));   // blob+16
            p.WriteUInt32((uint)Math.Max(0, maxHp));   // blob+20
            p.WriteUInt32((uint)Math.Max(0, maxSp));   // blob+24
        });
        return pw.ToArray();
    }

    /// <summary>msgTag of 2494 OnDelEffect.</summary>
    internal const ushort MsgTagDelEffect = 58313; // 0xE3C9

    /// <summary>Body length of 2494 in bytes.</summary>
    internal const int DelEffectBodyLen = 26;

    /// <summary>
    /// Builds RMI 2494 OnDelEffect — an effect or buff leaving an actor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single builder for this opcode.
    /// </para>
    /// <para>
    /// Layout: <c>[compactLen][u16 selfLen=24][u16 msgTag=58313][u32 0][u32 effectId]
    /// [u32 actorHandle][u64 0]</c>. The envelope is marker 1, declared length equal to the blob length, and the
    /// u16 at blob+0 equal to the blob length; blob+4 is zero. The trailing 8 bytes are constant zero and are
    /// never read, so they are sent as zero rather than modelled.
    /// </para>
    /// <para>
    /// The effect id comes first and the handle second, the opposite of its twin 2492 OnAddEffect, which leads
    /// with the handle. Getting this backwards inverts the message. The id at blob+8 resolves in
    /// <c>data/Effect_Table.json</c>; the actor handle sits at blob+12.
    /// </para>
    /// <para>
    /// Lifecycle: every effect removed by 2494 is expected to have been added earlier by 2492.
    /// </para>
    /// <para>
    /// Recipient: broadcast to everyone who can see the actor, including the actor itself, not a unicast to
    /// whoever asked. NPCs are valid subjects, which no per-owner unicast could serve.
    /// <c>BroadcastToNearbyAsync</c> excludes the source session and is the wrong helper; use
    /// <c>BroadcastToMapAsync</c> with no exclusion.
    /// </para>
    /// <para>
    /// Ordering: when the affected actor is a PC, 2493 OnDelEffectSuccess must be unicast to that PC immediately
    /// before this broadcast. See <c>EffectPackets.BuildDelEffectSuccess2493</c>. For an NPC subject, broadcast
    /// 2494 alone.
    /// </para>
    /// </remarks>
    public static byte[] BuildDelEffect2494(uint effectId, uint actorHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDelEffect);   // blob+2
            p.WriteUInt32(ReservedZero);  // blob+4
            p.WriteUInt32(effectId);      // blob+8  -- effect FIRST
            p.WriteUInt32(actorHandle);   // blob+12 -- handle SECOND
            p.WriteUInt64(ReservedZero);  // blob+16 trailing 8 zero bytes, never read
        });
        return pw.ToArray();
    }

    /// <summary>msgTag of 2435 OnZenyChange.</summary>
    internal const ushort MsgTagZenyChange = 58310; // 0xE3C6

    /// <summary>Body length of 2435 in bytes.</summary>
    internal const int ZenyChangeBodyLen = 22;

    /// <summary>
    /// Builds RMI 2435 OnZenyChange, the player's money changed. This is the main zeny-wallet update opcode
    /// (not a siege-only message, despite the siege-named client helpers): the u64 at blob+8 is stored as the
    /// player's zeny balance, which the client's affordability checks read. The initial balance arrives
    /// inside the 2310 head at body+112 (see WorldEntryService.WriteCharacterDetail); 2435 keeps it in sync
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: <c>[compactLen][u16 selfLen=20][u16 msgTag=58310][u32 actorHandle][u64 zeny]
    /// [u32 diamonds]</c>, 22 bytes.
    /// </para>
    /// <para>
    /// The actor handle sits at blob+4, where most of this family carries a constant zero. Assuming the usual
    /// "blob+4 is zero, payload starts at blob+8" shape would shift every field by four bytes.
    /// </para>
    /// <para>
    /// Money is 64-bit, not two separate fields. blob+12 is the high dword of the zeny total, which is why it is
    /// almost always zero; reading it as a reason code is a mistake. blob+16 is a separate currency, the diamond
    /// balance.
    /// </para>
    /// <para>
    /// The value is a balance, not a delta. A negative delta written as an unsigned integer would put roughly
    /// 4.29 billion on the wire, so callers pass the new balance.
    /// </para>
    /// </remarks>
    public static byte[] BuildZenyChange2435(uint actorHandle, long zeny, uint diamonds = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagZenyChange);               // blob+2
            p.WriteUInt32(actorHandle);                    // blob+4, NOT a zero slot
            p.WriteUInt64((ulong)Math.Max(0, zeny));       // blob+8, 64-bit
            p.WriteUInt32(diamonds);                       // blob+16
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Applies a zeny delta to the player and builds the 2435 notify carrying the NEW BALANCE.
    /// </summary>
    /// <remarks>
    /// Applying the delta here keeps the wire value and the server's own state in agreement, which is the
    /// minimum for a balance-carrying message to mean anything.
    /// <para>
    /// A negative delta goes through <see cref="Models.PlayerState.TryDebit"/>: when the wallet cannot pay it the
    /// balance is left unchanged (it is never clamped to 0, which would have forgiven the unpaid part).
    /// </para>
    /// </remarks>
    public static byte[] ApplyZenyDeltaAndBuild(Models.PlayerState player, long delta)
    {
        if (delta >= 0) player.Credit(delta);
        else if (delta != long.MinValue) player.TryDebit(-delta);
        return BuildZenyChange2435(player.EntityId, player.Gold);
    }

    /// <summary>msgTag of 2392 OnDeadNotify.</summary>
    internal const ushort MsgTagDeadNotify = 58059; // 0xE2CB

    /// <summary>Body length of 2392 in bytes.</summary>
    internal const int DeadNotifyBodyLen = 22;

    /// <summary>
    /// Builds RMI 2392 OnDeadNotify — an actor died.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single builder for this opcode.
    /// </para>
    /// <para>
    /// Layout: <c>[compactLen 01 14][u16 selfLen=20][u16 msgTag=58059][u32 0][u32 dyingActorHandle]
    /// [u32 killerHandle][u32 lethalDamage]</c>. blob+4 is zero.
    /// </para>
    /// <para>
    /// The client reads exactly one field: the u32 at blob+8, which it resolves through the actor registry and
    /// transitions to the dead state. blob+12 and blob+16 are never touched by the client.
    /// </para>
    /// <para>
    /// blob+8 is the corpse (the dying actor) and blob+12 is the killer, or zero for a killerless death.
    /// </para>
    /// <para>
    /// blob+16 is the damage of the killing blow, that is, of the hit that took the victim to 0 HP. It is zero
    /// for killerless PC deaths. The client never reads it, so it is wire fidelity rather than behaviour.
    /// </para>
    /// <para>
    /// Recipient: broadcast to the whole map with no exclusion; the killer must see the corpse fall. Never route
    /// this through <c>BroadcastToNearbyAsync</c>, which excludes the source session. A malformed body here does
    /// not kill one client, it kills every client in range at once.
    /// </para>
    /// <para>
    /// Emit sites: <c>NpcCombatService.SendPlayerDeathAsync</c> sends the player deaths (victim = the PC,
    /// killer = the monster, blob+16 = the lethal hit's damage). It is the only place this server lets a player
    /// die; DoTs (EffectService) and falls (FallDamageHandler) still clamp at 1 HP, so killerless PC deaths are
    /// never sent. <c>LootService.ProcessNpcCorpseAsync</c> sends the monster deaths and does not pass
    /// <paramref name="lethalDamage"/>, so those carry 0.
    /// </para>
    /// </remarks>
    /// <param name="lethalDamage">The damage of the hit that took the victim to 0 HP (blob+16).</param>
    public static byte[] BuildDeadNotify2392(uint victimHandle, uint killerHandle, uint lethalDamage = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDeadNotify);   // blob+2
            p.WriteUInt32(ReservedZero);    // blob+4
            p.WriteUInt32(victimHandle);    // blob+8  the dying actor (the only field the client reads)
            p.WriteUInt32(killerHandle);    // blob+12 killer, or zero for a killerless death
            p.WriteUInt32(lethalDamage);    // blob+16, never read by the client
        });
        return pw.ToArray();
    }
}
