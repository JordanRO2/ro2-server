using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;
using RO2.Combats.Domain;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host facade over one <see cref="SkillMoveLedger"/> per PlayerState: the per-caster state of the ground-object
/// skill moves (the cast target that lets a 1523 move an actor, and the actor each skill is dragging). The ledger
/// logic and its locking live in <see cref="SkillMoveLedger"/> (Combat.Domain); this maps player → ledger through
/// a <see cref="ConditionalWeakTable{TKey,TValue}"/> (so an entry lives exactly as long as the session's player
/// object and a logout leaves nothing behind) and owns the reach table that bounds how far a move may reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the move entry is needed.</b> Both requests come from the client's ground-object skill effect
/// update. On each tick the client moves the dragged actor to the effect's position and sends 1523 with
/// that handle at +0. When the effect ends it sends 1525, which carries only the skill and the end point,
/// not the handle. So the handle has to come from the 1523s before it. The order is N x 1523, then one
/// 1525.
/// </para>
/// <para>
/// <b>Why the cast record is needed, and why it is the 1508 target.</b> The dragged actor is set from the
/// cast target only for the move kinds 9, 14, 17, 20 and 22 (kinds 15, 18 and 19 leave it unset, and 16/21
/// end the cast). Every cast that carries on sends a 1508 ReqSkillStart with that same target at +16. So
/// the handle a legitimate 1523 names is always the +16 target of the 1508 that started the cast, for the
/// same skill id (group*100 + level on both sides), and a newer cast replaces it on the client as it does
/// here.
/// </para>
/// </remarks>
internal static class SkillMoveTracker
{
    /// <summary>The most casts held per caster; see <see cref="SkillMoveLedger.MaxCastsPerCaster"/>.</summary>
    internal const int MaxCastsPerCaster = SkillMoveLedger.MaxCastsPerCaster;

    /// <summary>The most moves held per caster; see <see cref="SkillMoveLedger.MaxMovesPerCaster"/>.</summary>
    internal const int MaxMovesPerCaster = SkillMoveLedger.MaxMovesPerCaster;

    private static readonly ConditionalWeakTable<PlayerState, SkillMoveLedger> Store = new();

    private static SkillMoveLedger LedgerOf(PlayerState caster) => Store.GetValue(caster, _ => new SkillMoveLedger());

    /// <summary>
    /// Records the accepted 1508 ReqSkillStart of <paramref name="skillId"/> on <paramref name="targetHandle"/>
    /// (request +16) as <paramref name="caster"/>'s cast of that skill, replacing the previous cast of the SAME skill
    /// (a cast of another skill does not evict it: the ground effect of a move skill outlives the next cast). Called by
    /// SkillStartHandler past its cooldown refusal: a refused cast ends on the client (2715 non-zero code ->
    /// Handle_SkillEndCasting), so it starts no ground effect.
    /// </summary>
    internal static void RecordCast(PlayerState caster, uint mapId, uint skillId, uint targetHandle)
        => LedgerOf(caster).RecordCast(mapId, skillId, targetHandle, DateTime.UtcNow);

    /// <summary>
    /// Whether <paramref name="handle"/> is the target of <paramref name="caster"/>'s accepted cast of exactly
    /// <paramref name="skillId"/>, made on <paramref name="mapId"/>. A zero handle never is.
    /// </summary>
    internal static bool IsCastTarget(PlayerState caster, uint mapId, uint skillId, uint handle)
        => Store.TryGetValue(caster, out var l) && l.IsCastTarget(mapId, skillId, handle);

    /// <summary>
    /// Whether <paramref name="caster"/> has an accepted cast of exactly <paramref name="skillId"/> made on
    /// <paramref name="mapId"/>, whatever its target (the self-move check of 1523/1525, where the moved actor is
    /// the caster and the 1508 target is not the handle).
    /// </summary>
    internal static bool IsCastSkill(PlayerState caster, uint mapId, uint skillId)
        => Store.TryGetValue(caster, out var l) && l.IsCastSkill(mapId, skillId);

    /// <summary>How many moves <paramref name="caster"/> holds.</summary>
    internal static int MoveCount(PlayerState caster)
        => Store.TryGetValue(caster, out var l) ? l.MoveCount() : 0;

    /// <summary>
    /// Records (or replaces) the move of <paramref name="skillId"/> cast by <paramref name="caster"/>. A skill already
    /// held is always replaced; a new one is refused (false, nothing stored) once the caster holds
    /// <see cref="MaxMovesPerCaster"/>.
    /// </summary>
    internal static bool Record(PlayerState caster, uint skillId, SkillMoveLedger.SkillMove move)
        => LedgerOf(caster).Record(skillId, move);

    /// <summary>Removes and returns the move of <paramref name="skillId"/>; false when no 1523 recorded one.</summary>
    internal static bool TryTake(PlayerState caster, uint skillId, out SkillMoveLedger.SkillMove move)
    {
        move = default;
        return Store.TryGetValue(caster, out var l) && l.TryTake(skillId, out move);
    }

    // ---- Reach ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The move kinds for which the client keeps the cast target as the dragged actor: 9, 14, 17, 20 and 22
    /// (see the class remarks). Under any other kind there is no dragged actor, so a 1523 naming an NPC under
    /// it cannot come from the client.
    /// </summary>
    /// <remarks>The kind is the client's claim, carried in the same request: this is a consistency check, not
    /// proof. The server holds no copy of the move kind (it is no column of Skill_Table_Base).</remarks>
    internal static bool IsTargetMoveKind(byte moveKind) => SkillMoveLedger.IsTargetMoveKind(moveKind);

    /// <summary>The Skill_Table_Base columns SkillData does not carry.</summary>
    private sealed record SkillReachRow(uint Skill_Group, float Skill_Range, float Influence_Bound_Length);

    /// <summary>
    /// Read once from the same "data" directory Program.cs hands GameDataStore (the pattern GiveEffectToItemHandler
    /// and InitGrindingOptHandler use for ItemInfo columns ItemData lacks).
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<uint, float>> ReachTable =
        new(() => LoadReach(Path.Combine("data", "Skill_Table_Base.json")));

    /// <summary>
    /// How far from its caster a skill of group <paramref name="skillGroup"/> can act: Skill_Range (how far its
    /// target may stand) plus Influence_Bound_Length (how far its area reaches past that), plus
    /// <see cref="AutoAttackState.AttackRangeTolerance"/>, the slack the client's own range check
    /// adds for the target's radius. Null when the group has no row, which refuses the move.
    /// </summary>
    /// <remarks>
    /// This is a server-side bound taken per skill from the table, so a skill with a 10-unit range cannot
    /// carry a monster 200 units away; the largest bound in the table is 100 + 100.
    /// </remarks>
    internal static float? ReachFor(uint skillGroup)
        => (ReachTableOverride ?? ReachTable.Value).TryGetValue(skillGroup, out var reach) ? reach + AutoAttackState.AttackRangeTolerance : null;

    /// <summary>
    /// Test seam: the table <see cref="ReachFor"/> reads on the CURRENT thread instead of the cwd-relative file, which a
    /// test host (running from its output directory, not the repo root) cannot find. Per-thread, so parallel tests do
    /// not see each other's table; null (the default, and always in production) reads <see cref="ReachTable"/>.
    /// </summary>
    [ThreadStatic]
    internal static IReadOnlyDictionary<uint, float>? ReachTableOverride;

    /// <summary>
    /// The reach the 1509/1549 damage gate (<see cref="ActorGate.SkillReach"/>) uses for a cast of group
    /// <paramref name="skillGroup"/>: <see cref="ReachFor"/> (Skill_Range + Influence_Bound_Length + tolerance),
    /// EXCEPT a <c>Skill_Range</c> of exactly 100 is the table's "no limit" marker and returns
    /// <see cref="float.PositiveInfinity"/> so the range check is
    /// skipped. Null when the group has no row, which the gate refuses as <see cref="ActorRefusal.RangeUnknown"/>.
    /// </summary>
    internal static float? CastReachFor(uint skillGroup)
        => CastReachTable.Value.TryGetValue(skillGroup, out var reach) ? reach : null;

    private static readonly Lazy<IReadOnlyDictionary<uint, float>> CastReachTable =
        new(() => LoadCastReach(Path.Combine("data", "Skill_Table_Base.json")));

    /// <summary>The damage-gate reach per Skill_Group from the Skill_Table_Base.json at <paramref name="path"/>:
    /// <see cref="float.PositiveInfinity"/> for a Skill_Range of 100 (the table's unlimited marker), else
    /// Skill_Range + Influence_Bound_Length + <see cref="AutoAttackState.AttackRangeTolerance"/>. Empty when the file
    /// is missing or unreadable (every gated cast is then refused as range-unknown).</summary>
    internal static IReadOnlyDictionary<uint, float> LoadCastReach(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<uint, float>();
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<SkillReachRow>>(File.ReadAllBytes(path));
            var byGroup = new Dictionary<uint, float>();
            foreach (var r in rows ?? new List<SkillReachRow>())
            {
                if (r.Skill_Range == 100f)
                {
                    byGroup.TryAdd(r.Skill_Group, float.PositiveInfinity);
                    continue;
                }
                var reach = r.Skill_Range + r.Influence_Bound_Length + AutoAttackState.AttackRangeTolerance;
                if (float.IsFinite(reach) && reach >= 0f)
                    byGroup.TryAdd(r.Skill_Group, reach);
            }
            return byGroup;
        }
        catch
        {
            return new Dictionary<uint, float>();
        }
    }

    /// <summary>Skill_Range + Influence_Bound_Length by Skill_Group from the Skill_Table_Base.json at
    /// <paramref name="path"/>; empty when the file is missing or unreadable (every NPC move is then refused).</summary>
    internal static IReadOnlyDictionary<uint, float> LoadReach(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<uint, float>();
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<SkillReachRow>>(File.ReadAllBytes(path));
            var byGroup = new Dictionary<uint, float>();
            foreach (var r in rows ?? new List<SkillReachRow>())
            {
                var reach = r.Skill_Range + r.Influence_Bound_Length;
                if (float.IsFinite(reach) && reach >= 0f)
                    byGroup.TryAdd(r.Skill_Group, reach);
            }
            return byGroup;
        }
        catch
        {
            return new Dictionary<uint, float>();
        }
    }
}
