using RO2.GameData;

namespace RO2.Skills.Domain;

/// <summary>
/// Pure talent (skill-point) rules over the <c>Talent_Table</c>: the free starter groups of a class, the set of
/// talent groups, a group's cast level cap, and the points an allocation has cost. The host owns the spend flow
/// (persisting/sending); this is the table math only. The RO2.GameData kernel is an allowed Domain reference.
/// </summary>
public static class TalentRules
{
    /// <summary>
    /// The skills a class holds for free at level 1, which is what a skill reset leaves it with: the
    /// <c>Talent_Table</c> rows that <paramref name="job"/> may use (bit <c>job-1</c> of <c>Require_Job</c>),
    /// that are not the job-master skill (<c>ISJobMaster</c> 0) and that need no other skill (all three
    /// <c>Need_Skill_Group</c> 0), and of those only the ones at the lowest <c>Base_Level</c> among them.
    /// Ascending. Empty for a job with no such row (Novice).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A skill reset leaves a job with this set (covered: jobs 1, 3, 4, 6, 7, 9, 10, 12, 13, 16, 17 and 18). Among
    /// them: job 4 -> [4101, 4103, 4108]; job 18 -> [6301, 6309, 6317]; job 6 -> [1102, 1110, 1144], the Swordsman
    /// rows, whose Require_Job carries jobs 1, 6 and 7 (the job-master skill 1200, ISJobMaster 1, goes); and the
    /// quest job changes to 10 -> [3104, 3107, 3124] and 17 -> [6101, 6104, 6111], rows whose Job_ID is the base
    /// class (3, 16). The job-6 reset and the job-10 change also carry the profession skill 50300, which has no
    /// Talent_Table row and is not touched by a reset.
    /// </para>
    /// <para>
    /// The lowest-Base_Level cut is what job 9 needs, and job 9 is the only job whose candidates span two
    /// Base_Levels, so it changes no other job's set. Its candidates are the Acolyte rows 2100, 2102 and 2103
    /// (Base_Level 1, Require_Job 386 = jobs 2, 8 and 9) and its own 2312 (Job_ID 9, Require_Job 256, no
    /// prerequisite, Base_Level 25). A job-9 reset leaves [210001, 210201, 210301] with no 2312. Granting 2312 would
    /// add 231201 to the reset's 2488, seed it on every job-9 login and make its first level free. The cut is not
    /// "Base_Level at most the character's level", nor "Base_Level 1": jobs 11, 15 and 18 have only Base_Level-25
    /// candidates, and a job-18 reset keeps all three of [6301, 6309, 6317].
    /// </para>
    /// <para>
    /// Grouping the table by <c>Job_ID</c> instead gives the second classes 6-15 their job-master row (job 6:
    /// [1200]) and Crescentia (17) nothing, because their roots are the base class's rows; JobInfo's
    /// Base_Job_ID is 0 on every row, so it cannot walk them back. The mask also keeps apart what the
    /// Job_ID grouping cannot say: the Archer rows name jobs 3 and 10 but not Beastmaster (11), and the
    /// Alter rows 16 and 17 but not Soul Maker (18), which keep none of them.
    /// </para>
    /// </remarks>
    public static uint[] StarterSkillGroups(GameDataStore store, byte job)
    {
        if (job is 0 or > 32) return [];
        var bit = 1u << (job - 1);
        var candidates = new List<(uint Group, byte BaseLevel)>();
        byte floor = byte.MaxValue;
        foreach (var r in store.TalentTable)
        {
            if ((r.Require_Job & bit) == 0 || r.ISJobMaster != 0) continue;
            if (r.Need_Skill_Group_1 != 0 || r.Need_Skill_Group_2 != 0 || r.Need_Skill_Group_3 != 0) continue;
            candidates.Add((r.Skill_Group_ID, r.Base_Level));
            if (r.Base_Level < floor) floor = r.Base_Level;
        }

        // Only the candidates at the lowest Base_Level among them (see the remarks: job 9's 2312).
        var groups = new SortedSet<uint>();
        foreach (var (group, baseLevel) in candidates)
            if (baseLevel == floor) groups.Add(group);
        var result = new uint[groups.Count];
        groups.CopyTo(result);
        return result;
    }

    /// <summary>The skill groups that are bought with talent points: every <c>Talent_Table</c> row, of any class.</summary>
    public static HashSet<uint> TalentSkillGroups(GameDataStore store)
    {
        var groups = new HashSet<uint>();
        foreach (var r in store.TalentTable) groups.Add(r.Skill_Group_ID);
        return groups;
    }

    /// <summary>
    /// The learned groups a skill reset drops: the talent-tree groups the character holds that are not among the
    /// job's starter set. Skills with no <c>Talent_Table</c> row (profession skills, e.g. 50300) are not talent
    /// groups, so they are kept. The <paramref name="learnedGroups"/> enumeration order is preserved.
    /// </summary>
    public static uint[] GroupsDroppedByReset(GameDataStore store, byte job, IEnumerable<uint> learnedGroups)
    {
        var starters = StarterSkillGroups(store, job);
        var talentGroups = TalentSkillGroups(store);
        return learnedGroups.Where(g => talentGroups.Contains(g) && Array.IndexOf(starters, g) < 0).ToArray();
    }

    /// <summary>
    /// True when <paramref name="skillGroup"/> is a talent skill (any <c>Talent_Table</c> row of any class names it),
    /// with <paramref name="max"/> the highest <c>Max_Skill_Level</c> among its rows. A group whose rows all carry
    /// <c>Max_Skill_Level</c> 0 (no cap recorded) reports <see cref="byte.MaxValue"/>, so <c>level &lt;= max</c> is a
    /// safe test for every caller. False (and <c>max</c> 0) for a group with no row: a non-talent, guild, item or
    /// monster skill. Unlike the per-job allocation legality check (host StatService) this is class-agnostic: it
    /// bounds a CAST, where the per-job cap is already enforced when the point is spent.
    /// </summary>
    public static bool TryGetSkillLevelLimit(GameDataStore store, uint skillGroup, out byte max)
    {
        max = 0;
        if (skillGroup > ushort.MaxValue) return false;
        var found = false;
        foreach (var r in store.TalentTable)
        {
            if (r.Skill_Group_ID != skillGroup) continue;
            found = true;
            if (r.Max_Skill_Level > max) max = r.Max_Skill_Level;
        }
        if (found && max == 0) max = byte.MaxValue;
        return found;
    }

    /// <summary>
    /// Talent points <paramref name="learned"/> has cost a character of <paramref name="job"/>: the levels of
    /// its <c>Talent_Table</c> skills, less the free first level of each starter it holds
    /// (<see cref="StarterSkillGroups"/>). Skills with no Talent_Table row (the profession skills, e.g. 50300)
    /// cost none.
    /// </summary>
    /// <remarks>
    /// A job-10 change leaves [3104, 3107, 3124] and 50300 at level 1 and answers 2544 blob+10 = 24 at level 25: the
    /// profession level costs nothing. A job-6 reset leaves 1102, 1110 and 1144 at level 1 and 50300 at level 3, and
    /// the character's next 1310 (the full talent state) spends the whole pool (2543 blob+10 = 0) on rows summing to
    /// more levels: 1102 at level 2, which it held at 1, and rows it did not hold, 1200 among them. The starters'
    /// first level is free, the job-master row is not, and 50300 is not in the vector.
    /// </remarks>
    public static int TalentPointsSpent(GameDataStore store, byte job, IEnumerable<(uint Group, byte Level)> learned)
    {
        var tree = TalentSkillGroups(store);
        var starters = StarterSkillGroups(store, job);
        int spent = 0;
        foreach (var (group, level) in learned)
        {
            if (level == 0 || !tree.Contains(group)) continue;
            spent += level;
            if (Array.IndexOf(starters, group) >= 0) spent -= 1;
        }
        return Math.Max(0, spent);
    }

    /// <summary>
    /// Talent points left to spend: one per level after the first (the 2542 each level-up sends is level - 1
    /// while nothing is bought) minus <see cref="TalentPointsSpent"/>, clamped to the byte the client keeps.
    /// </summary>
    /// <remarks>
    /// Skill resets send level - 1 as the point count (42 at level 43, 49 at level 50, 24 at level 25 for job
    /// changes). A job-master Warrior at level 50 got 69: 20 more, from a source this server does not model.
    /// </remarks>
    public static byte TalentPointsRemaining(GameDataStore store, byte job, int level,
        IEnumerable<(uint Group, byte Level)> learned)
        => (byte)Math.Clamp(Math.Max(0, level - 1) - TalentPointsSpent(store, job, learned), 0, 255);
    /// <summary>
    /// Most (group, level) pairs one 1310 may carry: <c>Talent_Table.json</c> has 230 rows, one per skill group, and a
    /// request lists each group at most once, so a longer vector can only repeat groups or name junk.
    /// </summary>
    public const int MaxTalentAllocations = 230;
    /// <summary>
    /// Whether a 1310 ReqPCUseTalentPoint allocation is legal to persist for a character of
    /// <paramref name="job"/> at <paramref name="level"/> holding <paramref name="held"/> (its LearnedSkills,
    /// group -> level; null is an empty tree). Each requested <c>(group, newLevel)</c> pair must name a
    /// <c>Talent_Table</c> skill the job may use (bit <c>job-1</c> of <c>Require_Job</c>), at a level within
    /// <c>[1, Max_Skill_Level]</c> that is not below the level already held, the character must have reached the
    /// row's <c>Base_Level</c>, and the prerequisites <c>Need_Skill_Group_1..3</c> must be in the post-change tree
    /// (the held tree overlaid with the requested pairs) at their <c>_Level</c> or more. A pair with a zero group or
    /// level is ignored (the handler persists neither). The first violation sets <paramref name="reason"/> and
    /// returns false, so the handler can refuse the whole request without persisting any of it.
    /// </summary>
    /// <remarks>
    /// These gates block the forgeries a modified client can attempt on 1310: learning a skill that is not
    /// in the class's talent tree (including junk or other classes' ids), raising one past its cap, taking one
    /// below the level it unlocks at, buying a skill without its parent, or lowering a held level to get points back.
    /// The talent-point BUDGET is deliberately NOT a hard gate
    /// here: the earned pool this server models (<c>level - 1</c>) is known to undercount for some
    /// characters — a job-master Warrior can hold 69 points at level 50 where <c>level - 1</c> is
    /// 49, from a source not yet modelled (see <see cref="TalentPointsRemaining"/>) — so rejecting on it
    /// would refuse a legitimate allocation. The caller logs an over-budget spend instead, and
    /// <see cref="TalentPointsRemaining"/> already clamps the pool it reports to the client to zero.
    /// The monotonic rule is defensive: the client UI never stages a level below the one held, and a reset goes
    /// through the delete path (item 2685 / ItemUseHandler), never through a 1310.
    /// <c>Need_Quest_ID</c> is not enforced: no row of the shipped table sets it, and the completed-quest set lives in
    /// the quest repository, not in the player state, so it needs an async read this pure gate does not have.
    /// </remarks>
    public static bool IsTalentAllocationLegal(
        GameDataStore store, byte job, int level,
        IReadOnlyList<(uint Group, byte Level)> allocations,
        IReadOnlyDictionary<uint, byte>? held,
        out string reason)
    {
        if (job is 0 or > 32)
        {
            reason = $"job {job} is out of range";
            return false;
        }

        var bit = 1u << (job - 1);

        // The job's talent rows, group -> (cap, unlock level). A group shared by several of the job's rows
        // takes the highest cap and the lowest unlock among them.
        var rows = new Dictionary<uint, (byte Max, byte Base)>();
        var needs = new Dictionary<uint, List<(uint Group, byte Level)[]>>();
        foreach (var r in store.TalentTable)
        {
            if ((r.Require_Job & bit) == 0) continue;
            if (rows.TryGetValue(r.Skill_Group_ID, out var cur))
                rows[r.Skill_Group_ID] = (Math.Max(cur.Max, r.Max_Skill_Level), Math.Min(cur.Base, r.Base_Level));
            else
                rows[r.Skill_Group_ID] = (r.Max_Skill_Level, r.Base_Level);

            // Prerequisites of this row; a group listed by several of the job's rows is satisfied by any one of them.
            var row = new List<(uint, byte)>(3);
            if (r.Need_Skill_Group_1 != 0) row.Add((r.Need_Skill_Group_1, r.Need_Skill_Group_1_Level));
            if (r.Need_Skill_Group_2 != 0) row.Add((r.Need_Skill_Group_2, r.Need_Skill_Group_2_Level));
            if (r.Need_Skill_Group_3 != 0) row.Add((r.Need_Skill_Group_3, r.Need_Skill_Group_3_Level));
            if (!needs.TryGetValue(r.Skill_Group_ID, out var list)) needs[r.Skill_Group_ID] = list = new();
            list.Add(row.ToArray());
        }

        // The tree as it stands once the request is applied (a repeated group keeps its last level, as the handler's
        // dedup does), against which both the monotonic rule and the prerequisites are read.
        var after = new Dictionary<uint, byte>();
        if (held is not null) foreach (var (g, l) in held) after[g] = l;

        foreach (var (group, lvl) in allocations)
        {
            if (group == 0 || lvl == 0) continue;
            if (!rows.TryGetValue(group, out var row))
            {
                reason = $"skill group {group} is not a talent skill of job {job}";
                return false;
            }
            if (row.Max != 0 && lvl > row.Max)
            {
                reason = $"skill group {group} level {lvl} exceeds its max {row.Max}";
                return false;
            }
            if (level < row.Base)
            {
                reason = $"skill group {group} unlocks at character level {row.Base} (have {level})";
                return false;
            }
            if (held is not null && held.TryGetValue(group, out var heldLevel) && lvl < heldLevel)
            {
                reason = $"skill group {group} level {lvl} is below the held level {heldLevel}";
                return false;
            }
            after[group] = lvl;
        }

        foreach (var (group, lvl) in allocations)
        {
            if (group == 0 || lvl == 0) continue;
            var alternatives = needs[group];   // rows has the group, so does needs
            var met = false;
            foreach (var req in alternatives)
            {
                var ok = true;
                foreach (var (needGroup, needLevel) in req)
                {
                    // A Need_Skill_Group this job cannot learn (not among its own talent rows) is a cross-job
                    // shared-row artifact, not a real prerequisite for this job - skip it, or the skill would be
                    // permanently unlearnable (e.g. group 6102's row names 6204, which belongs to another job's tree).
                    if (!rows.ContainsKey(needGroup)) continue;
                    if (!after.TryGetValue(needGroup, out var have) || have < Math.Max((byte)1, needLevel)) { ok = false; break; }
                }
                if (ok) { met = true; break; }
            }
            if (!met)
            {
                reason = $"skill group {group} is missing a prerequisite skill group";
                return false;
            }
        }

        reason = "";
        return true;
    }
}
