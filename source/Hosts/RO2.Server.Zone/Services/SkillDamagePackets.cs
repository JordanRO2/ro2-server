using System.Collections.Frozen;
using System.Text.Json;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// One floating-combat-text record: the 9-byte <c>[u8 kind][i32 damage][i32 extra]</c> triple that
/// the client pushes into <c>CCircularQueue</c> to draw a damage number over an actor.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuinely SHARED record type, not a coincidence of shape. It appears three times:
/// inline in 2495 (as the queue push <c>{1, blob+20, 0}</c> the handler builds by hand), as the
/// repeated tail of every 2718 target element, and embedded once in every 2797 target element. The
/// "extra" slot carries the same rare value set in 2718 and 2797.
/// </para>
/// <para>
/// Kind values seen in 2718 and 2797 are 0, 1, 2, 3, 4 and 6 (rarely 8 and 9). Damage may be zero,
/// positive or negative. The "extra" slot is almost always zero.
/// </para>
/// <para>
/// ⚠️ <b>What <c>kind</c> selects is NOT established.</b> The client only stores it in the queue
/// element, so no meaning is claimed for 0/1/2/3/4/6. <c>Kind = 0</c> is special in one respect: the
/// 2718 handler guards the queue push with <c>if (kind)</c>, so a record whose kind byte is zero is read off
/// the wire and then discarded.
/// </para>
/// </remarks>
/// <param name="Kind">Damage-flavour selector. See the remarks: meaning undetermined, 1 is modal in 2718.</param>
/// <param name="Damage">Signed damage. Positive in skill-hit records; see <see cref="SkillDamagePackets"/>.</param>
/// <param name="Extra">Second signed slot. Almost always zero.</param>
public readonly record struct SkillHitEffect(byte Kind, int Damage, int Extra = 0);

/// <summary>
/// One entry of RMI 2718's target vector: 25 fixed bytes plus a counted list of
/// <see cref="SkillHitEffect"/> records.
/// </summary>
/// <param name="TargetHandle">
/// The actor this hit landed on: a 0x13 NPC tag, a 0x11 PC tag, or zero.
/// </param>
/// <param name="TargetCurHp">That actor's current HP after the hit. See <see cref="SkillDamagePackets"/>.</param>
/// <param name="TargetCurSp">
/// That actor's current SP after the hit. The client reads it as a 16-bit value, which is why the value is clamped to 16 bits.
/// </param>
/// <param name="Effects">
/// The damage numbers to float over the target (0 to 10 per element in practice).
/// </param>
public readonly record struct SkillDamageHit(
    uint TargetHandle, uint TargetCurHp, uint TargetCurSp, IReadOnlyList<SkillHitEffect>? Effects = null);

/// <summary>
/// One entry of RMI 2797's target vector: a FIXED 25 bytes carrying exactly one embedded
/// <see cref="SkillHitEffect"/>. Unlike 2718's element it has no inner count.
/// </summary>
/// <param name="TargetHandle">
/// The actor this tick landed on: a 0x13 NPC or 0x11 PC tag, never zero.
/// </param>
/// <param name="TargetCurHp">That actor's current HP after the tick.</param>
/// <param name="TargetCurSp">
/// That actor's current SP after the tick; fits in 16 bits.
/// The client writes it into the TARGET's vitals, so 0 is not a neutral value: for an NPC
/// target a non-zero value is sent — see <see cref="SkillDamagePackets.NpcTargetSp"/>.
/// </param>
/// <param name="Effect">The single damage record. Kind 0 is discarded by the client.</param>
public readonly record struct SkillTickHit(
    uint TargetHandle, uint TargetCurHp, uint TargetCurSp, SkillHitEffect Effect);

/// <summary>
/// Wire builders for the skill-damage family: RMI 2718 NfySkillDamage, 2797 NfySkillDamageTick,
/// 2495 OnDotDamageNotify and 2709 NfySkillCastingStart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Four opcodes: 2718 NfySkillDamage, 2495 OnDotDamageNotify, 2797 NfySkillDamageTick and
/// 2709 NfySkillCastingStart. The layouts below are the wire layouts the client reads.
/// </para>
/// <para>
/// <b>All four are ENVELOPED</b> — <c>[compactLen][u16 selfLen][u16 msgTag][payload]</c> — the compact marker
/// is a legal width, the declared length equals the blob length, and <c>u16 at blob+0 == len(blob)</c>. The
/// marker width is exactly <c>len(blob) &lt; 0x80 ? 1 : 2</c>, which is what
/// <see cref="PacketWriter.WriteCompactLength"/> emits.
/// <b>This matters for more than correctness: a body the compact-length decoder rejects makes the
/// per-opcode stub push NULL into a consumer that dereferences it without a check — a malformed
/// body here is a remote client crash, not a dropped message.</b>
/// </para>
/// <para>
/// <b>Message tags</b>: 2718 = 58443 (0xE44B), 2495 = 58336 (0xE3E0), 2797 = 58479 (0xE46F), 2709 = 58440
/// (0xE448). All four also carry a zero u32 at blob+4.
/// </para>
/// <para>
/// <b>The skill id</b> field named <c>skillId</c> in each builder (2718 blob+8, 2797 blob+8, 2709 blob+12,
/// 2495 blob+16) is a row of the shipped table <c>data/Skill_Table_SkillLevel.json</c>; for 2495 a zero skill
/// id is also legal.
/// </para>
/// <para>
/// <b>Current HP / current SP.</b> The fields these messages feed to the client's HP and SP setters are the
/// actor's current HP (u32) and current SP (16-bit slot), the same pair 2401 OnActorAutoHeal and 2716 carry.
/// The narrow SP read is why every builder here clamps SP to 16 bits.
/// </para>
/// <para>
/// ⚠️ <b>2495's blob+24 is the victim's HP after the tick and blob+20 the signed delta,</b> but consecutive
/// 2495s for the same victim need not satisfy <c>hp_prev + damage == hp_next</c>: auto-heal, normal attacks
/// and other players' skills move the same actor's HP between two DoT ticks.
/// </para>
/// <para>
/// <b>Recipients.</b> All four are BROADCASTS and in all four the acting player is among the
/// recipients, so <c>BroadcastToNearbyAsync</c> — which EXCLUDES the source session — is the wrong
/// call for every one of them; use <c>BroadcastToMapAsync</c> with no exclusion.
/// </para>
/// </remarks>
public static class SkillDamagePackets
{
    // ---- Shared wire constants --------------------------------------------------------------------

    /// <summary>The reserved u32 at blob+4 of every message in this family; always zero.</summary>
    private const uint BlobReservedWord = 0;

    /// <summary>Flag byte value meaning "a target handle follows" (blob+16 of 2718, blob+28 of 2709).</summary>
    private const byte TargetPresent = 1;

    /// <summary>Flag byte value meaning "no target handle" (blob+16 of 2718, blob+28 of 2709).</summary>
    private const byte TargetAbsent = 0;

    /// <summary>The client reads the actor's SP as a 16-bit value, so every SP slot is clamped to this.</summary>
    private const uint MaxWireSp = ushort.MaxValue;

    /// <summary>Divisor that turns a skill id (group*100 + level) into its Skill_Group.</summary>
    private const uint SkillLevelsPerGroup = 100;

    private static ushort ClampSp(uint sp) => (ushort)Math.Min(sp, MaxWireSp);

    // =============================================================================================
    // 2718 NfySkillDamage
    // =============================================================================================

    /// <summary>Message tag of 2718: 58443 (0xE44B).</summary>
    internal const ushort MsgTagSkillDamage = 58443;

    /// <summary>Fixed part of 2718's blob, before the target vector: 46 bytes including the count.</summary>
    internal const int SkillDamageHeadLen = 46;

    /// <summary>Fixed part of one 2718 target element, before its effect list.</summary>
    internal const int SkillDamageElemLen = 25;

    /// <summary>Size of one <see cref="SkillHitEffect"/> on the wire.</summary>
    internal const int HitEffectLen = 9;

    /// <summary>Zero bytes between the SP slot and the effect count in a 2718 target element (elem+10..23).</summary>
    private const int SkillDamageElemZeroLen = 14;

    /// <summary>The u8 at blob+44 of 2718: a flag that is zero in practice.</summary>
    private const byte SkillDamageTrailingFlag = 0;

    /// <summary>
    /// 🔴 <b>HARD CAP — the client parses 2718 into a FIXED-SIZE STACK BUFFER and does not clamp
    /// the wire's counts.</b> Exceeding this is a remote stack-smash, not a rendering glitch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client parses 2718 into a fixed 4,148-byte stack buffer, writing element <i>i</i> at offset 43 with a
    /// stride of <b>205</b> and the wire's blob+42 / blob+44 fields at offsets 4143 / 4145. The capacity is
    /// therefore EXACTLY 20 elements (43 + 205·20 = 4143) and EXACTLY 20 effect records per element
    /// (25 + 9·20 = 205). The loop condition is on the raw wire byte — there is no clamp — so a 21st element
    /// writes about 200 bytes past the buffer, over the stack-protection cookie, the exception link, saved EBP
    /// and the return address. An effect count ≥ 21 on the LAST element does the same.
    /// </para>
    /// <para>
    /// The outer count ranges over 0..20 and the per-element effect count never exceeds 10 in practice. An AoE
    /// that hit 21 actors would crash every client on the map, which is why this is a throw and not a
    /// truncation.
    /// </para>
    /// </remarks>
    public const int MaxSkillDamageTargets = 20;

    /// <summary>Effect records per 2718 target element. Same buffer arithmetic — see
    /// <see cref="MaxSkillDamageTargets"/>. The practical maximum is 10.</summary>
    public const int MaxSkillDamageEffectsPerTarget = 20;

    /// <summary>
    /// The value sent in the unmodelled u16 at blob+42 for a PC cast.
    /// </summary>
    /// <remarks>
    /// The slot IS read: the client takes its low byte and passes it as an argument of the skill-effect lookup,
    /// where a value of 0 takes a different branch than a positive one. So it is not padding and it is not free.
    /// <b>One rule about it is established: an NPC caster carries 0.</b> For a PC caster the values seen are 1
    /// (most), 0 and 257. What selects between them is NOT determined: it is nearly, but not exactly, a function of
    /// the skill, and no shipped table column was shown to produce it. The modal PC value is sent and nothing is
    /// claimed about it.
    /// <b>For the answer to a PC's own 1509 the value IS known: it is the request's u16 at blob+43</b> (LE16 of
    /// request bytes 43..44), so SkillDamageHandler echoes it. This constant stays for the shapes that have no such
    /// request field read (the revival and emergency-call landings).
    /// </remarks>
    public const ushort SkillEffectIndexPcModal = 1;

    /// <summary>
    /// Builds RMI 2718 NfySkillDamage — the message that lands a skill's damage on every target it
    /// hit and floats the damage numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field order is the client's deserialiser read in order: the fixed head fields (<c>8, 4, 4, 1, 4, 12, 1,
    /// 8, 2, 1</c> bytes), then a <b>u8</b> count, followed by that many target elements; each element reads six
    /// u32, then a <b>u8</b> inner count, then that many 9-byte records.
    /// </para>
    /// <code>
    ///   [compactLen]                        1 byte when len(blob) &lt; 0x80, else 2
    ///   blob+0  u16 selfLen                 == len(blob)
    ///   blob+2  u16 msgTag  = 0xE44B        constant
    ///   blob+4  u32 0                       zero
    ///   blob+8  u32 skillId                 is a Skill_Table_SkillLevel row
    ///   blob+12 u32 casterHandle            valid actor tag
    ///   blob+16 u8  1 iff a primary target follows
    ///   blob+17 u32 primaryTargetHandle
    ///   blob+21 f32 x, y, z                 echo of 1509 blob+9..20 (zero when it names a target)
    ///   blob+33 u8  castType                echo of C2S 1509 +33
    ///   blob+34 u64 0                       echo of 1509 blob+34 — see remarks
    ///   blob+42 u16 skillEffectIndex        echo of 1509 LE16@43; see SkillEffectIndexPcModal
    ///   blob+44 u8  0                       almost always zero
    ///   blob+45 u8  count                   number of targets hit
    ///   blob+46 element[count], each:
    ///           +0  u32 targetHandle
    ///           +4  u32 targetCurHp
    ///           +8  u16 targetCurSp         see the width note below
    ///           +10 14 bytes 0              zero
    ///           +24 u8  effectCount
    ///           +25 effect[effectCount], each [u8 kind][i32 damage][i32 extra]
    /// </code>
    /// <para>
    /// <b>The length model.</b> <c>len(blob) == 46 + Σ(25 + 9·effectCount_i)</c> consumes the body EXACTLY, for
    /// outer counts 0..20 and effect counts 0..10.
    /// </para>
    /// <para>
    /// ⚠️ <b>WIDTH NOTE for elem+8.</b> The DESERIALISER reads six <b>u32</b>, so on the wire elem+8 is a 32-bit
    /// slot followed by 12 — not 14 — bytes before the count. The 16-bit width comes from the CONSUMER, which
    /// takes a 16-bit value. The emitted bytes are identical either way because the SP slot fits in 16 bits and the
    /// following bytes are zero, but the two layers must not be conflated: write SP clamped to 16 bits, and do
    /// not read the extra two bytes as "padding that can carry anything".
    /// </para>
    /// <para>
    /// <b>Field notes.</b> blob+16 equals <c>primaryTarget != 0</c>, which is why this builder DERIVES that byte
    /// instead of taking it. The caster handle at blob+12 carries an actor type tag (0x11 PC or 0x13 NPC) and the
    /// primary target at blob+17 does too, counting zero. The position triple at blob+21 is finite.
    /// </para>
    /// <para>
    /// <b>The pairing against the C2S request fixes the meaning of the head.</b> A 2718 answering a client's own
    /// 1509 ReqSkillDamage carries the skill id the receiver itself asked for, the primary target at blob+17 equal
    /// to the request's target at +5, and <c>castType</c> at blob+33 equal to the request's +33 — which is why this
    /// builder takes it as an echo. blob+42 is an echo of the request's u16 at +43, not +42
    /// (<see cref="SkillEffectIndexPcModal"/>).
    /// </para>
    /// <para>
    /// <b>RECIPIENT: broadcast to the map, and the caster is one of the recipients.</b> A skill's damage frames
    /// outnumber the requests that cause them, which a unicast cannot produce; a client is told about other
    /// players' skill damage; and the caster is NOT excluded: a 2718 with the same skill id arrives in the
    /// REQUESTING client's own stream. <c>BroadcastToNearbyAsync</c> excludes the source session and would drop
    /// exactly those.
    /// </para>
    /// <para>
    /// <b>Defaults.</b> With the defaults below the two undetermined slots are modal constants: blob+34 is usually
    /// zero and is never read by the consumer along any path that was followed, and blob+44 is almost always zero.
    /// </para>
    /// </remarks>
    /// <param name="casterHandle">The actor that used the skill. Carries its type tag.</param>
    /// <param name="skillId">The skill. Must be a row of Skill_Table_SkillLevel.</param>
    /// <param name="primaryTargetHandle">The main target, or 0 for a target-less cast.</param>
    /// <param name="hits">One entry per actor the skill hit, up to 20.</param>
    /// <param name="x">World X of the hit. For the answer to a PC's 1509 the whole triple is the request's f32
    /// triple at blob+9 (equal to 2718's +21..32; the request's +21 direction in 2): zero whenever the request has a
    /// primary target.</param>
    /// <param name="y">World Y.</param>
    /// <param name="z">World Z.</param>
    /// <param name="castType">
    /// Echo of C2S 1509 +33. Values are 0 (most), 2, 4, 1 and 3; the client switches on it to pick
    /// an effect-file suffix. Pass the request's byte through.
    /// </param>
    /// <param name="skillEffectIndex">See <see cref="SkillEffectIndexPcModal"/>. Pass 0 for an NPC caster.</param>
    /// <param name="unknown34">
    /// The u64 at blob+34: usually zero, otherwise a small integer. The consumer was not observed reading it. For
    /// the answer to a PC's 1509 it is the request's u64 at blob+34, echoed; zero, the modal value, elsewhere.
    /// </param>
    public static byte[] BuildSkillDamage2718(
        uint casterHandle, uint skillId, uint primaryTargetHandle,
        IReadOnlyList<SkillDamageHit>? hits,
        float x = 0f, float y = 0f, float z = 0f,
        byte castType = 0, ushort skillEffectIndex = SkillEffectIndexPcModal, ulong unknown34 = 0)
    {
        var list = hits ?? Array.Empty<SkillDamageHit>();
        if (list.Count > MaxSkillDamageTargets)
        {
            throw new ArgumentException(
                $"2718 carries at most {MaxSkillDamageTargets} targets; {list.Count} would overflow the " +
                "client's fixed-size parse buffer (elements are stored at offset 43 with a 205-byte " +
                "stride, so a 21st element runs past the end of the buffer).",
                nameof(hits));
        }

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillDamage);                             // blob+2
            p.WriteUInt32(BlobReservedWord);                              // blob+4
            p.WriteUInt32(skillId);                                       // blob+8
            p.WriteUInt32(casterHandle);                                  // blob+12
            p.WriteByte(primaryTargetHandle != 0 ? TargetPresent : TargetAbsent); // blob+16 derived
            p.WriteUInt32(primaryTargetHandle);                           // blob+17
            p.WriteSingle(x);                                             // blob+21
            p.WriteSingle(y);                                             // blob+25
            p.WriteSingle(z);                                             // blob+29
            p.WriteByte(castType);                                        // blob+33
            p.WriteUInt64(unknown34);                                     // blob+34
            p.WriteUInt16(skillEffectIndex);                              // blob+42
            p.WriteByte(SkillDamageTrailingFlag);                         // blob+44
            p.WriteByte((byte)list.Count);                                // blob+45 u8 count
            foreach (var h in list)
            {
                var fx = h.Effects ?? Array.Empty<SkillHitEffect>();
                if (fx.Count > MaxSkillDamageEffectsPerTarget)
                {
                    throw new ArgumentException(
                        $"2718 carries at most {MaxSkillDamageEffectsPerTarget} effect records per target; " +
                        $"{fx.Count} overruns the client's 205-byte per-element slot.",
                        nameof(hits));
                }
                p.WriteUInt32(h.TargetHandle);                            // elem+0
                p.WriteUInt32(h.TargetCurHp);                             // elem+4
                p.WriteUInt16(ClampSp(h.TargetCurSp));                    // elem+8, client reads u16
                p.WriteZeros(SkillDamageElemZeroLen);                     // elem+10
                p.WriteByte((byte)fx.Count);                              // elem+24
                foreach (var e in fx)
                {
                    p.WriteByte(e.Kind);
                    p.WriteInt32(e.Damage);
                    p.WriteInt32(e.Extra);
                }
            }
        });
        return pw.ToArray();
    }

    // =============================================================================================
    // 2797 NfySkillDamageTick
    // =============================================================================================

    /// <summary>Message tag of 2797: 58479 (0xE46F).</summary>
    internal const ushort MsgTagSkillDamageTick = 58479;

    /// <summary>Fixed part of 2797's blob, including the u16 count: 19 bytes.</summary>
    internal const int SkillDamageTickHeadLen = 19;

    /// <summary>Size of one 2797 target element — FIXED, unlike 2718's.</summary>
    internal const int SkillDamageTickElemLen = 25;

    /// <summary>Zero bytes between the SP slot and the embedded effect in a 2797 target element (elem+10..15).</summary>
    private const int SkillDamageTickElemZeroLen = 6;

    /// <summary>
    /// Cap on 2797's target count. Unlike 2718 this is NOT a memory-safety limit — the client pushes into a heap
    /// <c>std::vector</c>, so a large count is merely large — but the maximum seen in practice is 20, and staying
    /// inside that envelope is the whole point of this file.
    /// </summary>
    public const int MaxSkillDamageTickTargets = 20;

    /// <summary>
    /// The SP to put in a target element when the target is an NPC: the value this server already told
    /// the client that NPC has, i.e. 2506's record+54 maxSp (NpcSpawnService.WriteNpcRecord).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every NPC-target (0x13) element of 2797 carries a NON-zero SP, constant per handle, and equal to the SP of
    /// that same handle's 2506/2317 record (where curSp +50 and maxSp +54 are equal). So an NPC's SP is not spent
    /// by damage, and the tick restates the SP the client already holds. Sending 0 instead would overwrite it with
    /// a value never sent for an NPC. 2718's target element (same u16 at elem+8) behaves identically.
    /// </para>
    /// <para>
    /// Callers: SkillDamageTickHandler (2797 elem+8) and SkillDamageHandler (2718 elem+8) — every
    /// target either handler emits is an NPC resolved through NpcStateManager.
    /// </para>
    /// <para>
    /// The value is <see cref="NpcRuntimeState.WireMaxSp"/>, the same property WriteNpcRecord writes at +54, so the
    /// tick, 2643 and 2495 restate exactly what the record advertised. NpcStateManager resolves it from the
    /// per-(asset, level) vitals table (<see cref="NpcRetailVitals"/>), with a stated fallback for an asset the
    /// table does not cover; a state whose SP was never resolved keeps the old mirror of max HP.
    /// </para>
    /// </remarks>
    public static uint NpcTargetSp(NpcRuntimeState npc) => npc.WireMaxSp;

    // ---- Which 1549 ticks get a 2797 ------------------------------------------------------------

    /// <summary>Skill_Table_Base.Skill_Type of an attack skill: the only type whose 1549 ticks this
    /// server answers with a damaging 2797. See <see cref="IsDamageTickSkill"/>.</summary>
    internal const int AttackSkillType = 1;

    /// <summary>
    /// The file GameDataStore loads Skill_Table_Base from, resolved against the same working directory
    /// (Program.cs hands <c>LoadAsync</c> the relative directory "data"; RevivalSkillPackets reads the
    /// same file the same way).
    /// </summary>
    private static readonly string ShippedSkillTable = Path.Combine("data", "Skill_Table_Base.json");

    private static readonly Lazy<(FrozenDictionary<uint, int> Types, string? Error)> ShippedSkillTypes =
        new(LoadShippedSkillTypes);

    /// <summary>
    /// True when a C2S 1549 ReqSkillDamageTick for <paramref name="skillId"/> (group*100+level) is answered
    /// with a 2797 that DAMAGES its targets: the skill's Skill_Table_Base.Skill_Type is 1 (attack).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 1549 is answered with a damaging 2797 only for attack skills, by the skill's Skill_Type:
    /// </para>
    /// <list type="bullet">
    /// <item>type 1 (attack): answered. Every element of a PC caster's type-1 2797 carries kind 1, 2, 3 or 4 and a
    /// damage &gt;= 0. Kinds 0 and 6 occur only with NPC casters (boss-skill ticks with a zero amount), which a PC's
    /// 1549 never produces.</item>
    /// <item>types 2, 4 and 10 (buffs, auras, channelled self effects): not answered with a 2797. For example 631910
    /// Cure (type 4) has its effect reach the wire as 2495 heal ticks instead.</item>
    /// <item>type 3 (support: heals such as 510610 Highness Heal, 511505 Oratio): answered with PC targets and
    /// NEGATIVE amounts (heals) for a PC caster. This server has no heal model, so these are not answered either,
    /// rather than hitting a monster with a heal.</item>
    /// </list>
    /// <para>
    /// The handler used to answer every 1549 by damaging the caster's current combat target, so a Cure
    /// tick, an aura tick or a heal tick hit the monster the player was fighting.
    /// An unknown group, or a table that could not be read, counts as "not an attack skill" (no answer);
    /// see <see cref="SkillTypeTableError"/>.
    /// </para>
    /// </remarks>
    public static bool IsDamageTickSkill(uint skillId) =>
        ShippedSkillTypes.Value.Types.TryGetValue(skillId / SkillLevelsPerGroup, out var type) && type == AttackSkillType;

    /// <summary>Why the Skill_Type column could not be read, or null when it was.</summary>
    public static string? SkillTypeTableError => ShippedSkillTypes.Value.Error;

    /// <summary>
    /// Skill_Table_Base.Skill_Type of <paramref name="skillId"/>'s group (skill id / 100), or null when the group is
    /// unknown or the column could not be read. SkillDamageHandler picks its 2718 element rule from it
    /// (<see cref="SkillDamagePcElements"/>).
    /// </summary>
    public static int? SkillTypeOf(uint skillId) =>
        ShippedSkillTypes.Value.Types.TryGetValue(skillId / SkillLevelsPerGroup, out var type) ? type : null;

    /// <summary>Skill_Group → Skill_Type for every Skill_Table_Base row that carries both.</summary>
    /// <remarks>The curated <c>SkillData</c> model does not carry Skill_Type, so the column is read from
    /// the same JSON GameDataStore loads, once.</remarks>
    internal static FrozenDictionary<uint, int> ParseSkillTypes(ReadOnlySpan<byte> skillTableJson)
    {
        var reader = new Utf8JsonReader(skillTableJson, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var types = new Dictionary<uint, int>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Skill_Type", out var t) && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var type)
                && row.TryGetProperty("Skill_Group", out var g) && g.TryGetUInt32(out var group))
            {
                types[group] = type;
            }
        }
        return types.ToFrozenDictionary();
    }

    private static (FrozenDictionary<uint, int>, string?) LoadShippedSkillTypes()
    {
        try
        {
            var types = ParseSkillTypes(File.ReadAllBytes(ShippedSkillTable));
            return types.Count > 0
                ? (types, null)
                : (types, $"{ShippedSkillTable} has no Skill_Type column");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenDictionary<uint, int>.Empty, $"{ShippedSkillTable} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds RMI 2797 NfySkillDamageTick — one periodic tick of a channelled or over-time skill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire layout is the client's deserialiser read in order: <c>8, 4, 1, 4</c> bytes and then a vector whose
    /// count is a <b>u16</b> and whose element is a flat 25-byte record. The client then walks that vector with a
    /// stride of 25, resolves each element's first u32 to an actor, feeds the HP and SP fields to the vitals
    /// setters, and hands the 9-byte damage record at elem+16 to the hit-effect spawner.
    /// </para>
    /// <code>
    ///   [compactLen]                        1 byte when len(blob) &lt; 0x80, else 2
    ///   blob+0  u16 selfLen                 == len(blob)
    ///   blob+2  u16 msgTag  = 0xE46F        constant
    ///   blob+4  u32 0                       zero
    ///   blob+8  u32 skillId                 is a Skill_Table_SkillLevel row
    ///   blob+12 u8  tickSequence            echo of C2S 1549 +4
    ///   blob+13 u32 casterHandle            valid actor tag
    ///   blob+17 u16 count                   NEVER zero
    ///   blob+19 element[count], each 25 bytes:
    ///           +0  u32 targetHandle
    ///           +4  u32 targetCurHp
    ///           +8  u16 targetCurSp
    ///           +10 6 bytes 0               zero
    ///           +16 u8  kind
    ///           +17 i32 damage
    ///           +21 i32 extra
    /// </code>
    /// <para>
    /// <b>Length model.</b> <c>len(blob) == 19 + 25·count</c> for counts 1..15 and 20. The compact marker widens
    /// from one byte to two once the blob reaches 128 bytes.
    /// </para>
    /// <para>
    /// ⚠️ <b>The u16 count is a HARD read.</b> The client's vector reader fails if its 2-byte count read fails — a
    /// body that ends before the count makes the client abandon the vector. The usual "SHORT is not a
    /// refutation" caveat for this protocol does not apply to this field.
    /// </para>
    /// <para>
    /// <b>Field notes.</b> The caster handle at blob+13 carries an actor tag (0x11 PC or 0x13 NPC). Against the
    /// client's own C2S 1549 ReqSkillDamageTick (<c>[u32 skillId][u8 tick]</c>), the skill id is one the receiver
    /// itself asked for and the tick byte at blob+12 matches the request's, which is what identifies blob+12 as the
    /// tick sequence rather than a flag.
    /// </para>
    /// <para>
    /// <b>RECIPIENT: broadcast to the map, caster included.</b> Tick frames outnumber the requests that cause them;
    /// a client sees ticks from more than one PC caster; and a 2797 with the same (skillId, tick) reaches the
    /// requesting client itself. Do not exclude the caster.
    /// </para>
    /// <para>
    /// <b>There is no undetermined slot in this message</b>; every byte is built from these arguments.
    /// </para>
    /// </remarks>
    /// <param name="casterHandle">The actor channelling the skill.</param>
    /// <param name="skillId">The skill. Always a Skill_Table_SkillLevel row.</param>
    /// <param name="tickSequence">
    /// The tick counter echoed from C2S 1549 +4 (1 most often, up to 15). Pass the request's byte through rather
    /// than inventing a counter.
    /// </param>
    /// <param name="hits">
    /// One entry per actor the tick hit. The vector is never empty on the wire (the count is at least 1), so an
    /// empty list is refused.
    /// </param>
    public static byte[] BuildSkillDamageTick2797(
        uint casterHandle, uint skillId, byte tickSequence, IReadOnlyList<SkillTickHit>? hits)
    {
        if (hits is null || hits.Count == 0)
            throw new ArgumentException(
                "2797 always carries at least one target",
                nameof(hits));
        if (hits.Count > MaxSkillDamageTickTargets)
        {
            throw new ArgumentException(
                $"2797 carries at most {MaxSkillDamageTickTargets} targets. " +
                "This is an envelope limit, not a client-memory one.",
                nameof(hits));
        }

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillDamageTick);                         // blob+2
            p.WriteUInt32(BlobReservedWord);                              // blob+4
            p.WriteUInt32(skillId);                                       // blob+8
            p.WriteByte(tickSequence);                                    // blob+12
            p.WriteUInt32(casterHandle);                                  // blob+13
            p.WriteUInt16((ushort)hits.Count);                            // blob+17 u16 count
            foreach (var h in hits)
            {
                p.WriteUInt32(h.TargetHandle);                            // elem+0
                p.WriteUInt32(h.TargetCurHp);                             // elem+4
                p.WriteUInt16(ClampSp(h.TargetCurSp));                    // elem+8
                p.WriteZeros(SkillDamageTickElemZeroLen);                 // elem+10
                p.WriteByte(h.Effect.Kind);                               // elem+16
                p.WriteInt32(h.Effect.Damage);                            // elem+17
                p.WriteInt32(h.Effect.Extra);                             // elem+21
            }
        });
        return pw.ToArray();
    }

    // =============================================================================================
    // 2495 OnDotDamageNotify
    // =============================================================================================

    /// <summary>Message tag of 2495: 58336 (0xE3E0).</summary>
    internal const ushort MsgTagDotDamageNotify = 58336;

    /// <summary>Blob length of 2495: 30 bytes, i.e. a 32-byte body.</summary>
    internal const int DotDamageNotifyBlobLen = 30;

    /// <summary>
    /// Builds RMI 2495 OnDotDamageNotify — one damage-over-time (or heal-over-time) application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client passes the blob straight to the DoT handler, so its offsets ARE blob offsets. It reads blob+8,
    /// +12, +16, +20, +24 and the u16 at +28 — every byte of the 30-byte blob except the tag prologue and the
    /// zero u32 at +4.
    /// </para>
    /// <code>
    ///   [compactLen 01 1E]
    ///   blob+0  u16 selfLen = 30            constant
    ///   blob+2  u16 msgTag  = 0xE3E0        constant
    ///   blob+4  u32 0                       zero
    ///   blob+8  u32 victimHandle            the actor whose HP/SP this message sets
    ///   blob+12 u32 sourceHandle            the actor credited with the damage
    ///   blob+16 u32 skillId                 0 or a real skill row
    ///   blob+20 i32 damage                  SIGNED, and never zero
    ///   blob+24 u32 victimCurHp
    ///   blob+28 u16 victimCurSp
    /// </code>
    /// <para>
    /// <b>Which handle is the victim and which is the source</b> — both slots carry a mix of PC and NPC tags, so
    /// the tag cannot decide it. The handler resolves blob+8 to an actor, writes THAT actor's current HP and SP
    /// from blob+24 and blob+28, and then calls its damage-display routine with (victim, blob+12, flavour 4,
    /// blob+16, damage &lt;= 0). The normal-attack handler calls the same routine as (target, attackerHandle,
    /// flavour 1, …), so blob+12 is the source here. The third argument, 1 for a normal attack and 4 here, is the
    /// damage-flavour selector.
    /// </para>
    /// <para>
    /// <b>Field notes.</b> The victim handle carries an actor type tag (0x11 PC or 0x13 NPC). The source handle
    /// usually does, but a small fraction carry low byte 0x4D, so do not assert the tag on this field. The skill id
    /// at blob+16 is zero or a real Skill_Table_SkillLevel row. The damage at blob+20 is <b>never zero</b>, either
    /// sign (negative more often), and the handler branches on <c>damage &lt;= 0</c>, so the sign is a live field
    /// and not an accident of encoding.
    /// </para>
    /// <para>
    /// <b>RECIPIENT: broadcast to the map; both the victim and the source are among the recipients.</b> 2495 has
    /// no C2S trigger — it is server-timed. A client is told about damage-over-time between two third parties,
    /// which only a broadcast produces.
    /// </para>
    /// <para>
    /// <b>Every byte of this message is modelled</b>; there is no constant sent on faith.
    /// </para>
    /// </remarks>
    /// <param name="victimHandle">The actor taking the tick. Its HP and SP are what the client writes.</param>
    /// <param name="sourceHandle">The actor credited with it. May equal the victim.</param>
    /// <param name="skillId">The skill or effect that applied it, or 0.</param>
    /// <param name="damage">
    /// Signed. The DoT sign convention is the OPPOSITE of the skill-hit records in 2718/2797:
    /// here negative values are the norm, there positive ones are. Pass
    /// the value in the convention the client branches on — <c>damage &lt;= 0</c> selects one path.
    /// </param>
    /// <param name="victimCurHp">The victim's current HP after the tick.</param>
    /// <param name="victimCurSp">The victim's current SP after the tick. Clamped: the client reads 16 bits.</param>
    public static byte[] BuildDotDamageNotify2495(
        uint victimHandle, uint sourceHandle, uint skillId, int damage,
        uint victimCurHp, uint victimCurSp)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDotDamageNotify);                         // blob+2
            p.WriteUInt32(BlobReservedWord);                              // blob+4
            p.WriteUInt32(victimHandle);                                  // blob+8
            p.WriteUInt32(sourceHandle);                                  // blob+12
            p.WriteUInt32(skillId);                                       // blob+16
            p.WriteInt32(damage);                                         // blob+20
            p.WriteUInt32(victimCurHp);                                   // blob+24
            p.WriteUInt16(ClampSp(victimCurSp));                          // blob+28
        });
        return pw.ToArray();
    }

    // =============================================================================================
    // 2709 NfySkillCastingStart
    // =============================================================================================

    /// <summary>Message tag of 2709: 58440 (0xE448).</summary>
    internal const ushort MsgTagSkillCastingStart = 58440;

    /// <summary>Blob length of 2709: 63 bytes, i.e. a 65-byte body.</summary>
    internal const int SkillCastingStartBlobLen = 63;

    /// <summary>Bytes at blob+45..56 of 2709 that are not interpreted; sent as zero.</summary>
    private const int SkillCastingStartUndeterminedLen = 12;

    /// <summary>The reserved u32 at blob+57 of 2709; always zero.</summary>
    private const uint SkillCastingStartTrailingWord = 0;

    /// <summary>
    /// The value placed in the u16 at 2709 blob+61.
    /// </summary>
    /// <remarks>
    /// It is an ECHO, not a constant: it equals the u16 the client itself sent at C2S 1506 +41. The values seen
    /// are 257 (most) and 0. 257 is 0x0101, the same value the 2715 variant slot takes occasionally; what selects
    /// between 257 and 0 is not determined, so the caller should pass the request's u16 through and this default
    /// is only the fallback.
    /// </remarks>
    public const ushort CastingVariantModal = 257;

    /// <summary>
    /// Builds RMI 2709 NfySkillCastingStart — the message that starts everyone's cast bar for one
    /// actor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client passes the blob straight to the cast-start handler, so every offset in it IS a blob offset. It
    /// resolves blob+8 to an actor, feeds {blob+8, blob+29} to the skill map, and fills the cast-bar object with
    /// blob+12, blob+29, the float triples at blob+33 and blob+45, the float at blob+24 and the u16 at blob+61.
    /// </para>
    /// <code>
    ///   [compactLen 01 3F]
    ///   blob+0  u16 selfLen = 63            constant
    ///   blob+2  u16 msgTag  = 0xE448        constant
    ///   blob+4  u32 0                       zero
    ///   blob+8  u32 casterHandle            valid actor tag
    ///   blob+12 u32 skillId                 is a Skill_Table_SkillLevel row
    ///   blob+16 u64 castId                  echo of C2S 1506 +4
    ///   blob+24 f32 castSeconds
    ///   blob+28 u8  1 iff a target follows
    ///   blob+29 u32 targetHandle
    ///   blob+33 f32 x, y, z                 echo of C2S 1506 +17
    ///   blob+45 f32, f32, f32               UNDETERMINED, usually zero — see remarks
    ///   blob+57 u32 0                       zero
    ///   blob+61 u16 castingVariant          echo of C2S 1506 +41
    /// </code>
    /// <para>
    /// <b>The u64 at blob+16 is the 64-bit cast id the client generates and sends in its own C2S 1506
    /// ReqSkillCastingStart at +4</b>; a 2709 answering the client's own request carries it back. On such a
    /// pair, four separate fields agree exactly:
    /// </para>
    /// <list type="bullet">
    /// <item><c>skillId</c> at blob+12 equals the request's +0.</item>
    /// <item><c>targetHandle</c> at blob+29 equals the request's +13.</item>
    /// <item>The float triple at blob+33 equals the request's +17 triple, bit for bit.</item>
    /// <item>The u16 at blob+61 equals the request's +41.</item>
    /// </list>
    /// <para>
    /// <b>Field notes.</b> blob+28 equals <c>targetHandle != 0</c>, so this builder derives it. The caster handle
    /// carries an actor tag (0x11 PC or 0x13 NPC) and the target slot does too, counting zero. The wire's
    /// <paramref name="castSeconds"/> is at most the shipped <c>Skill_Table_SkillLevel.Casting_Time</c> and usually
    /// shorter: the cast time is the table value shortened by something not recovered, exactly as 2715's cooldown
    /// is, so the table value is its ceiling.
    /// </para>
    /// <para>
    /// ⚠️ <b>blob+45 is UNDETERMINED and this builder sends zero.</b> The consumer reads all three floats. The
    /// triple is usually all-zero; when it is not, the components are small signed values. It is NOT a unit
    /// direction and not a world position either. Nothing is claimed about it; zero is the modal value.
    /// </para>
    /// <para>
    /// <b>RECIPIENT: broadcast to the map, caster included.</b> Cast-start frames outnumber the requests that cause
    /// them; a client receives 2709 for more than one distinct PC caster; and the caster gets its own — the unique
    /// cast id comes back to the sending client. The paired unicast is 2708 AnsSkillCastingStart; 2709 is not it.
    /// </para>
    /// <para>
    /// <b>Reproduction.</b> The only slot not derived is the blob+45 triple; no other slot in this message
    /// disagrees.
    /// </para>
    /// </remarks>
    /// <param name="casterHandle">The actor starting the cast.</param>
    /// <param name="skillId">The skill. Always a Skill_Table_SkillLevel row.</param>
    /// <param name="castId">
    /// The 64-bit token from C2S 1506 +4. Echo it; do not generate one. It is the field that makes
    /// the client's own cast bar line up with the broadcast.
    /// </param>
    /// <param name="castSeconds">
    /// Cast duration. Bounded above by <c>Skill_Table_SkillLevel.Casting_Time</c>; pass the table value unless a
    /// haste model exists.
    /// </param>
    /// <param name="targetHandle">The cast target, or 0.</param>
    /// <param name="x">World X, echoed from C2S 1506 +17.</param>
    /// <param name="y">World Y, echoed from +21.</param>
    /// <param name="z">World Z, echoed from +25.</param>
    /// <param name="castingVariant">See <see cref="CastingVariantModal"/> — echo of C2S 1506 +41.</param>
    public static byte[] BuildSkillCastingStart2709(
        uint casterHandle, uint skillId, ulong castId, float castSeconds, uint targetHandle,
        float x, float y, float z, ushort castingVariant = CastingVariantModal)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillCastingStart);                       // blob+2
            p.WriteUInt32(BlobReservedWord);                              // blob+4
            p.WriteUInt32(casterHandle);                                  // blob+8
            p.WriteUInt32(skillId);                                       // blob+12
            p.WriteUInt64(castId);                                        // blob+16
            p.WriteSingle(castSeconds);                                   // blob+24
            p.WriteByte(targetHandle != 0 ? TargetPresent : TargetAbsent);// blob+28 derived
            p.WriteUInt32(targetHandle);                                  // blob+29
            p.WriteSingle(x);                                             // blob+33
            p.WriteSingle(y);                                             // blob+37
            p.WriteSingle(z);                                             // blob+41
            p.WriteZeros(SkillCastingStartUndeterminedLen);               // blob+45
            p.WriteUInt32(SkillCastingStartTrailingWord);                 // blob+57
            p.WriteUInt16(castingVariant);                                // blob+61
        });
        return pw.ToArray();
    }
}
