using System.Buffers.Binary;
using RO2.Combats.Domain;
using RO2.Combats.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1508 ReqSkillStart — the message that actually starts a cast, including a basic attack.
/// </summary>
/// <remarks>
/// <para>
/// The basic attack is a SKILL. The order is
/// <c>C2S 1498 ReqNormalAttackStart → C2S 1508 ReqSkillStart → [windup] → C2S 1509 ReqSkillDamage</c>,
/// and the windup only begins once the server has answered 1508. The answer must therefore use the exact
/// 2715 and 2716 body shapes; otherwise the client never gets the grant, never leaves the run animation,
/// and only reaches the swing when some later message shakes it loose.
/// </para>
/// <para>
/// The answer: after each request the server sends 2715 and 2716. 2717 AnsSkillError is the
/// refusal path and is deliberately not sent here.
/// </para>
/// <para>
/// The request layout is fixed at 47 bytes (a single <c>ReadBytesAligned(47)</c> on the client side):
/// <c>+0 u32 skillId | +4 f32 caster XYZ | +16 u32 target handle | +20 u8 flag | +21 u64 |
/// +29 f32 angle | +33 f32 XYZ | +45 u16 variant</c>.
/// </para>
/// <para>
/// The answer ECHOES three of those: 2715 +6 and 2716 blob+49 are request +16, 2715's vec3 is
/// request +33, and 2715 +27 / 2716 blob+53 are request +45. The variant selects the skill-effect row the
/// cast plays (a value &lt;= 0 means "the skill's default row") and takes the values 0, 1 and 257, so it
/// must be echoed rather than a constant. The client builds +45 itself, and for some cast types it then
/// sends the 1509 with +6 as its primary target (1509 +5).
/// </para>
/// </remarks>
public class SkillStartHandler : ZoneRmiHandlerBase<SkillStartHandler>
{
    /// <summary>Fixed request size, forced by the client's reader.</summary>
    private const int RequestLen = 47;

    /// <summary>Window within which repeated 1508s for the same skill group count as ONE activation's
    /// burst. A single cast often fires 2+ 1508 frames (typically 50-200 ms apart),
    /// so SP is spent only on the first and the burst duplicates are not re-charged.</summary>
    private static readonly TimeSpan SkillStartDebounce = TimeSpan.FromMilliseconds(350);

    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillStart;
    public override string Name => "SkillStart";

    public SkillStartHandler(IServiceScopeFactory s, ILogger<SkillStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Time source for the burst-debounce window and the cooldown stamps. The real clock in production;
    /// a test sets it so the "within <see cref="SkillStartDebounce"/> of the last start" condition does not hinge on
    /// how much wall-clock time elapsed during the run — the landing (SkillCastLandings) decides a burst duplicate's
    /// answer, not elapsed time (SkillCastLandings remarks: no time window can stand in for the 1509).</summary>
    internal Func<DateTime> Clock { get; init; } = static () => DateTime.UtcNow;

    /// <summary>The request fields that 2715 and 2716 carry back unchanged.</summary>
    internal readonly record struct AnswerEcho(uint TargetHandle, float X, float Y, float Z, ushort Variant);

    /// <summary>
    /// Reads <see cref="AnswerEcho"/> from a 1508 body of at least <see cref="RequestLen"/> bytes.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>+16, the target, goes to 2715 +6 and 2716 blob+49 (SkillPackets builders).</item>
    /// <item>+33, the second triple, goes to the 2715 vec3, which is the request's second triple and not
    /// the caster's position.</item>
    /// <item>+45, the variant, goes to 2715 +27 and 2716 blob+53 (class remarks), in refusals as well.</item>
    /// </list>
    /// </remarks>
    internal static AnswerEcho ReadAnswerEcho(ReadOnlySpan<byte> req) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(req[16..]),
        BinaryPrimitives.ReadSingleLittleEndian(req[33..]),
        BinaryPrimitives.ReadSingleLittleEndian(req[37..]),
        BinaryPrimitives.ReadSingleLittleEndian(req[41..]),
        BinaryPrimitives.ReadUInt16LittleEndian(req[45..]));

    /// <summary>
    /// The 1508's f32 at +29, which 2716 carries back at blob+32 (with the +33 triple at blob+36..47)
    /// (SkillPackets.BuildUseSkillStart2716).
    /// </summary>
    internal static float ReadCastAngle(ReadOnlySpan<byte> req) => BinaryPrimitives.ReadSingleLittleEndian(req[29..]);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLen) return;
        var req = payload.Span;

        var skillId = BinaryPrimitives.ReadUInt32LittleEndian(req);
        // Target, position triple and variant go back to the client unchanged in every frame below,
        // the cooldown refusal included (ReadAnswerEcho).
        var (targetHandle, posX, posY, posZ, variant) = ReadAnswerEcho(req);
        var castAngle = ReadCastAngle(req);

        // Cooldown seconds: the real, Vigor-reduced value, not the raw table ceiling. The cooldown
        // is a character-global multiplicative reduction of the table value, floored at exactly 0.25x
        // (the AoV Vigor 75% cap), and equal to the table only for Vigor-0 characters. The client stores
        // whatever float we send in 2715 verbatim (it does no stat math of its own), so the server is the
        // sole authority for the enforced cooldown. Compute the reduced value from the caster's effective
        // Vigor and enforce the SAME value below, so client and server agree by construction.
        var store = services.GetRequiredService<GameDataStore>();

        uint skillGroup = skillId / 100u;

        // Preconditions, before any pending cast, SP spend, cooldown arm or RecentSkillStart write (CastGate):
        //  - an ITEM skill (ItemInfo.Skill_ID_1 >= 2,000,000) never starts through a raw 1508. It is authorised only
        //    by RMI 1561 ReqSkillStart_Item, which verifies the caster owns the item that carries the skill and
        //    consumes it before recording the pending cast that its 1509 then lands. A forged 1508 would otherwise
        //    record a FREE pending cast (no item held/consumed), which a forged 1509 lands for the effect. Guild
        //    (4,000,10x) and monster skills are not ItemInfo.Skill_ID_1, so they still start here.
        //  - a dead caster casts nothing;
        //  - a TALENT class skill (a Talent_Table group below FirstNonClassSkillId, HasTalentLimit) must be learned
        //    (LearnedSkills[group] > 0) and have a Skill_Table_SkillLevel row for the exact id. Non-talent, guild, item
        //    and monster skills are exempt: never in LearnedSkills, and the 1561 path vouches for the item ones. A raw
        //    1508 of any other id with no table row still gets cooldown 0 / SP 0 here.
        // Every refusal is the silent illegitimate-skill-action answer: a 2715 carrying 20152 (the 1509 refusal's,
        // SkillDamageHandler.RefuseUnauthorizedAsync) echoing the request's target/vec/variant, and nothing else.
        var gate = CastGate.Check(new SkillCatalog(store), skillId, player.IsDead,
            player.LearnedSkills.GetValueOrDefault(skillGroup));
        if (gate != CastGate.Decision.Allowed)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsUseSkillStart,
                SkillPackets.BuildUseSkillStartAck2715(skillId, targetHandle, 0f, posX, posY, posZ,
                    variant: variant, resultCode: SkillPackets.SkillResultOnCooldown));
            Logger.LogDebug("{Name}: 1508 skill {SkillId} refused ({Why})", player.Name, skillId, gate switch
            {
                CastGate.Decision.ItemSkill => "item skill; starts only through 1561",
                CastGate.Decision.Dead => "caster is dead",
                _ => "talent skill not learned or no table row",
            });
            return;
        }

        store.SkillLevels.TryGetValue(skillId, out var levelData);

        var tableCooldown = levelData?.Cooldown_Time ?? 0f;
        // Exempt classes that do not follow the Vigor combat reduction: 9000xxx/9216 skills are
        // never reduced (ratio 1.0), and 20xxxxx item-special skills use a separate rule that is not
        // modelled yet: send the table value for those (self-consistent: send == enforce).
        bool cooldownExempt = skillId >= 2_000_000u || skillGroup == 9216u;
        var cooldown = cooldownExempt
            ? tableCooldown
            : CooldownRules.EffectiveCooldown(tableCooldown, player.Level, player.CombatVigor);

        // ---- SP COST (the live cast path) --------------------------------------------------------------
        // SP is spent here, at cast start (1508), on the path players actually use; the older 1364 ReqSkill
        // handler is not used by the client.
        //  * The client reflects SP from 2716: it writes blob+20 (low u16) into the caster's vitals and the SP
        //    gauge reads that field. So deducting player.Mp before the 2716 build below drops the on-screen SP
        //    bar with no extra packet. (blob+16 = HP is applied unconditionally by the same handler, but the
        //    2716 below already carries the real current HP, so the HP bar does not desync.)
        //  * A single activation can emit 2+ 1508 frames within ~300 ms. Charging per-frame
        //    would double-deduct AND, for a high-SP skill, the duplicate would see Mp < cost and cancel the
        //    cast the first frame started, so the burst is debounced by skill group and only the first
        //    frame spends SP.
        //  * spCost == 0 skills (basic attack 410801, buffs, toggles, self-cast) are never
        //    gated: the guard is `spCost > 0`.
        // Cooldown is enforced server-side, safely: because the client stores and honours exactly the
        // cooldown we send it in 2715 (above), the value we enforce here is the value the client enforces,
        // so a legit client never trips this; only a modified client casting inside its own cooldown does.
        int spCost = levelData?.Use_SP ?? 0;
        var nowUtc = Clock();
        bool isBurstDuplicate = player.RecentSkillStart.TryGetValue(skillGroup, out var lastStart)
            && (nowUtc - lastStart) < SkillStartDebounce;
        // RecentSkillStart is NOT written here: only an ACCEPTED frame records it (below the cooldown refusal). A
        // refused frame that refreshed it would leave a key held down inside the debounce as a "burst duplicate"
        // forever, answered with silence.

        // COOLDOWN REJECT: a cast that arrives while the skill group is still cooling. End the client's cast with a
        // refusing 2715 carrying cooldown 0, and stop BEFORE spending SP or broadcasting the cast. The code
        // is a choice between two (SkillCasterBusyWindow): 20032 (string_ui 2549, "Do not have
        // enough time to cast this skill to use.") when the caster is idle, i.e. its last accepted cast of any skill
        // began more than that skill's Casting_Time + Excution_Time ago; otherwise 20152 (silent: no toast; the
        // client still ends the cast).
        //
        // A BURST DUPLICATE (another 1508 of the group within the debounce of the last one) is answered by whether the
        // cast its first frame started has landed, i.e. whether the caster's 1509 has come since the accepted 1508
        // (SkillCastLandings). Landed: it is refused like any in-cooldown cast. Still in flight: it is answered
        // with nothing, no 2715, no 2962 and no 2716, and never with a second accept; a second accepting 2715 and
        // a second 2716 would be wrong.
        if (player.SkillCooldowns.TryGetValue(skillGroup, out var cdExpiry) && cdExpiry > nowUtc)
        {
            if (isBurstDuplicate && !SkillCastLandings.HasLanded(player, skillGroup))
            {
                Logger.LogDebug("{Name} skill {SkillId}: burst duplicate before the cast landed, not answered",
                    player.Name, skillId);
                return;
            }

            var refusalCode = SkillCasterBusyWindow.RefusalCode(player, nowUtc);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsUseSkillStart,
                SkillPackets.BuildUseSkillStartAck2715(skillId, targetHandle, 0f, posX, posY, posZ,
                    variant: variant, resultCode: refusalCode));

            // 2962 NfySkillStartError follows every refusing 2715. The 2715 is unicast, so the
            // caster is the only client that learns about the refusal from it. This broadcast is
            // how the other clients learn, and it goes out without an exclusion because the caster gets it
            // too, with the same code and skill id as the 2715 that preceded it. The client reads the
            // skill id and the caster handle, so both come from this cast.
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfySkillStartError,
                writer => writer.WriteBytes(SkillStartErrorPackets.BuildSkillStartError2962(
                    refusalCode, skillId, player.EntityId)));

            Logger.LogDebug("{Name} skill {SkillId} rejected with {Code}: still on cooldown ({Remaining:F2}s left)",
                player.Name, skillId, refusalCode, (cdExpiry - nowUtc).TotalSeconds);
            return;
        }

        // Past the refusal the cast is accepted: it is the frame RecentSkillStart records (the next 1508 of the group
        // within SkillStartDebounce is read against it above), and in flight until its 1509 (SkillDamageHandler marks it landed).
        player.RecentSkillStart[skillGroup] = nowUtc;
        SkillCastLandings.BeginCast(player, skillGroup);
        // The caster's last accepted cast, which picks the code of a later cooldown refusal. Recorded on every accepted
        // frame, burst duplicates included (SkillCasterBusyWindow remarks).
        SkillCasterBusyWindow.RecordAccepted(player, skillId, nowUtc);

        // Record the accepted cast so its 1509 ReqSkillDamage / 1549 tick can be
        // authorised and no forged damage packet lands without a start behind it (SkillDamageHandler and
        // SkillDamageTickHandler consume this via SkillCastLandings.TryGovern). Only the first frame of an
        // activation, like the SP spend and the cooldown arm below: the burst duplicates are the same cast, and one
        // 1509 consumes one entry.
        if (!isBurstDuplicate)
            SkillCastLandings.RecordPending(player, skillId, targetHandle, nowUtc);

        if (spCost > 0 && !isBurstDuplicate)
        {
            // The client pre-gates SP (it will not send 1508 without enough), so Mp < spCost is a rare edge.
            // Clamp to 0 rather than hard-reject: a clean reject needs a 2715 non-zero resultCode whose
            // "not enough SP" value is not defined here (guessing it shows the wrong toast, and
            // skipping 2715 hangs the client mid-cast). The reduced Mp is picked up
            // by the 2716 build below. Keep the clamp; do not add a below-SP refusal without the proper code.
            // The spend is atomic (PlayerState.ApplySpDelta clamps to [0, MaxMp] under the vitals lock).
            player.ApplySpDelta(-spCost);
        }

        // Track the (reduced) cooldown so the reject above can enforce it. Only on the first frame of an
        // activation (not the burst duplicates, which must not re-arm it), and only for skills that have one.
        if (!isBurstDuplicate && cooldown > 0f)
            player.SkillCooldowns[skillGroup] = nowUtc.AddSeconds(cooldown);

        // An accepted cast of a channelled skill (Skill_Duration > 0) opens the channel that the caster's 1609 will
        // complete or its 1440 cancel; the 2897 answer to that 1609 reports whether it is still the one in progress
        // (SkillChannelingPackets). Only here, past the cooldown refusal, and only on the first frame of the
        // activation: a refused cast opens nothing, and the burst duplicates name the same skill.
        if (!isBurstDuplicate && SkillChannelingPackets.IsChannelled(skillId))
            player.BeginChannel(skillId);
        // The accepted cast's target is the only actor its ground-object effect may drag (SkillMoveTracker).
        SkillMoveTracker.RecordCast(player, player.MapId, skillId, targetHandle);

        // 2715 AnsUseSkillStart: UNICAST to the caster. The client applies every field to the
        // local player with no handle check: it writes the cooldown into the local player's
        // cooldown map and, on a non-zero result code, ends the local player's cast. Broadcasting it
        // would put the skill on cooldown for everyone in range.
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsUseSkillStart,
            SkillPackets.BuildUseSkillStartAck2715(skillId, targetHandle, cooldown, posX, posY, posZ,
                variant: variant));

        // A cast that is not a riding skill ends the ride, between the 2715 and the 2716: a mounted rider's 1508
        // of any other skill is followed by its own drop (2497 2498 2493 2494 2712), while a riding-boost
        // 1508 (RidingSkill.Skill_ID) keeps the ride (RidingService.KeepsRide). A burst duplicate finds the ride
        // already dropped; DropAsync sends nothing when the player is not riding.
        if (!RidingService.KeepsRide(store, skillId))
            await RidingService.DropAsync(session, player, services);

        // 2716 NfyUseSkillStart: BROADCAST, and the caster is one of the recipients, so no
        // exclusion. The client resolves blob+12 to an actor and animates THAT actor,
        // bailing out when the handle is not in the receiver's registry, and it explicitly tests
        // whether the animated actor is the local player before refreshing a UI window, a test
        // that only makes sense for a message that also arrives for other actors. Excluding the caster,
        // as BroadcastToNearbyAsync would, would leave its own cast without the animation.
        //
        // blob+32 and blob+36..47 are the request's angle (+29) and triple (+33..44), echoed
        // (SkillPackets.BuildUseSkillStart2716). The extra-target vector (blob+55)
        // stays empty: filling it needs an area-target resolver (the skill's shape and radius against live
        // positions) this server does not have.
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyUseSkillStart,
            writer => writer.WriteBytes(SkillPackets.BuildUseSkillStart2716(
                player.EntityId, skillId,
                (uint)Math.Max(0, player.Hp), (uint)Math.Max(0, player.Mp),
                targetHandle, variant: variant,
                angle: castAngle, x: posX, y: posY, z: posZ)));

        // No timer here for the end of a channelled skill (Skill_Duration > 0: group 6304, plus 7372101).
        // The onlookers' 2586/2585 come from the caster's own 1609/1440 (SkillChannelingComplete/Cancel
        // handlers), not from a server clock; the 2897 answer to a 1609 is followed by a 2586
        // (SkillChannelingPackets). The channel runs on the caster's clock, and exactly one 2585 or 2586 is sent
        // per cast, so a timer here would duplicate the 1609-driven 2586.
        Logger.LogDebug("{Name} starts skill {SkillId} on 0x{Target:X8} (cooldown {Cd}s)",
            player.Name, skillId, targetHandle, cooldown);
    }
}
