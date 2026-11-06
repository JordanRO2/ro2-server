using RO2.GameData.Tables;

namespace RO2.Guilds.Domain;

/// <summary>Why a guild skill point-learn request was refused by the rules.</summary>
public enum GuildSkillLearnRefusal
{
    /// <summary>Not refused.</summary>
    None,
    /// <summary>More ids were requested than the guild has unspent points.</summary>
    NotEnoughPoints,
    /// <summary>
    /// An empty request, an unknown id, a non-point line, a level that is not the next of its line, or a guild level
    /// below the required level.
    /// </summary>
    WrongSkillIndex,
    /// <summary>The first required skill is neither held nor learned earlier in the request.</summary>
    MissingNeedSkill1,
    /// <summary>The second required skill is neither held nor learned earlier in the request.</summary>
    MissingNeedSkill2,
}

/// <summary>Outcome of using a guild skill book for a guild.</summary>
public enum GuildSkillBookVerdict
{
    /// <summary>The book's skill is learned.</summary>
    Learn,
    /// <summary>The guild already holds that level of the line or a higher one.</summary>
    AlreadyLearned,
    /// <summary>The previous level of the line is not held.</summary>
    PreviousLevelMissing,
    /// <summary>The book does not teach a skill from this path, or the guild level is below the required level.</summary>
    NotLearnable,
}

/// <summary>
/// Pure guild skill rules: skill id layout, unspent points, the point-learn plan, the skill-book verdict and the
/// member skill list with guild links. The skill rows are passed in as a table indexed by Guild_Skill_ID.
/// </summary>
public static class GuildSkillRules
{
    /// <summary>Skill_LearnType of the lines bought with guild skill points.</summary>
    public const byte PointLearnType = 0;

    /// <summary>Skill_LearnType of the guild skill lines that are learned from books.</summary>
    public const byte BookLearnType = 1;

    /// <summary>Guild_Skill_ID is <c>line * SkillIdLineStride + level</c>; a player Link_Skill_ID is built the same way.</summary>
    public const uint SkillIdLineStride = 100;

    /// <summary>Returns the line of a Guild_Skill_ID, which is the id without its level.</summary>
    public static uint LineOf(uint skillId) => skillId / 100;

    /// <summary>Returns the level of a Guild_Skill_ID within its line.</summary>
    public static int LevelOf(uint skillId) => (int)(skillId % 100);

    /// <summary>
    /// The largest <c>Skill_Value</c> of a learned skill in the given category, or 0 when no learned skill carries it.
    /// The guild panel (2559) reads category 7 as the supply capacity and category 8 as the siege-weapon capacity.
    /// </summary>
    public static uint MaxCategoryValue(IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table, byte category)
    {
        uint max = 0;
        foreach (var id in learned)
        {
            if (!table.TryGetValue(id, out var row)) continue;
            if (row.Skill_Category_1 == category) max = Math.Max(max, row.Skill_Value_1);
            if (row.Skill_Category_2 == category) max = Math.Max(max, row.Skill_Value_2);
            if (row.Skill_Category_3 == category) max = Math.Max(max, row.Skill_Value_3);
        }
        return max;
    }

    /// <summary>Returns the highest level held on each line, from a guild's learned ids.</summary>
    public static Dictionary<uint, int> LineLevels(IEnumerable<uint> learned)
    {
        var levels = new Dictionary<uint, int>();
        foreach (var id in learned)
        {
            var line = LineOf(id);
            if (LevelOf(id) > levels.GetValueOrDefault(line)) levels[line] = LevelOf(id);
        }
        return levels;
    }

    /// <summary>
    /// Returns the guild's unspent skill points: the guild level minus one, less one per level held on a point-cost
    /// line, never below 0.
    /// </summary>
    public static byte UnspentPoints(byte guildLevel, IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        var spent = 0;
        foreach (var (line, level) in LineLevels(learned))
            if (table.TryGetValue(line * 100 + (uint)level, out var row) && row.Skill_LearnType == PointLearnType)
                spent += level;
        return (byte)Math.Clamp(guildLevel - 1 - spent, 0, byte.MaxValue);
    }

    /// <summary>
    /// Returns the ids a point-learn request learns, in an order that satisfies their prerequisites, or null when
    /// refused, with the reason through <paramref name="refusal"/>. Nothing is learned from a refused request.
    /// </summary>
    public static List<uint>? PlanLearn(IReadOnlyList<uint> requested, IEnumerable<uint> learned, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table, out GuildSkillLearnRefusal refusal)
    {
        refusal = GuildSkillLearnRefusal.WrongSkillIndex;
        if (requested.Count == 0) return null;
        var held = learned.ToArray();

        if (requested.Count > UnspentPoints(guildLevel, held, table))
        {
            refusal = GuildSkillLearnRefusal.NotEnoughPoints;
            return null;
        }
        var levels = LineLevels(held);
        var pending = new List<uint>(requested);
        var accepted = new List<uint>(requested.Count);

        bool Holds(uint id) => id == 0 || levels.GetValueOrDefault(LineOf(id)) >= LevelOf(id);

        bool progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            for (int i = 0; i < pending.Count; i++)
            {
                var id = pending[i];

                if (!table.TryGetValue(id, out var row) || row.Skill_LearnType != PointLearnType) return null;
                if (guildLevel < row.Need_GuildLevel) return null;

                if (LevelOf(id) != levels.GetValueOrDefault(LineOf(id)) + 1) continue;
                if (!Holds(row.Need_Skill_ID_1) || !Holds(row.Need_Skill_ID_2)) continue;
                levels[LineOf(id)] = LevelOf(id);
                accepted.Add(id);
                pending.RemoveAt(i--);
                progress = true;
            }
        }
        if (pending.Count == 0)
        {
            refusal = GuildSkillLearnRefusal.None;
            return accepted;
        }

        var stuck = pending[0];
        var stuckRow = table[stuck];
        refusal = LevelOf(stuck) != levels.GetValueOrDefault(LineOf(stuck)) + 1 ? GuildSkillLearnRefusal.WrongSkillIndex
            : !Holds(stuckRow.Need_Skill_ID_1) ? GuildSkillLearnRefusal.MissingNeedSkill1
            : GuildSkillLearnRefusal.MissingNeedSkill2;
        return null;
    }

    /// <summary>
    /// Decides what a book skill does for a guild of the given level holding the given learned skills.
    /// <paramref name="skillId"/> is the book's Skill_ID_1.
    /// </summary>
    public static GuildSkillBookVerdict PlanBook(uint skillId, IEnumerable<uint> learned, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        if (skillId == 0 || !table.TryGetValue(skillId, out var row) || row.Skill_LearnType != BookLearnType)
            return GuildSkillBookVerdict.NotLearnable;
        if (guildLevel < row.Need_GuildLevel) return GuildSkillBookVerdict.NotLearnable;

        var levels = LineLevels(learned);
        bool Holds(uint id) => id == 0
            || levels.GetValueOrDefault(LineOf(id)) >= LevelOf(id);

        if (Holds(skillId)) return GuildSkillBookVerdict.AlreadyLearned;
        if (!Holds(row.Need_Skill_ID_1) || !Holds(row.Need_Skill_ID_2)) return GuildSkillBookVerdict.PreviousLevelMissing;
        return GuildSkillBookVerdict.Learn;
    }

    /// <summary>
    /// Builds a member's 2488 skill list: its own skills plus the link skill of the highest level the guild holds
    /// on each guild skill line. Each entry is a (group, level) pair, with id = group * 100 + level. When a group
    /// appears twice, the higher level is kept.
    /// </summary>
    public static IReadOnlyList<(uint Group, byte Level)> SkillListWithGuildLinks(
        IEnumerable<KeyValuePair<uint, byte>> ownSkills, IEnumerable<uint> guildSkills,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        var byGroup = new Dictionary<uint, byte>();
        void Add(uint group, byte level)
        {
            if (level > byGroup.GetValueOrDefault(group)) byGroup[group] = level;
        }
        foreach (var (group, level) in ownSkills) Add(group, level);
        foreach (var (line, level) in LineLevels(guildSkills))
            if (table.TryGetValue(line * SkillIdLineStride + (uint)level, out var row) && row.Link_Skill_ID != 0)
                Add(LineOf(row.Link_Skill_ID), (byte)LevelOf(row.Link_Skill_ID));
        return byGroup.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    /// <summary>
    /// The 2488 lists to send after a guild skill list answer. Empty when nothing was sent to the client yet
    /// (<paramref name="held"/> null) or it already holds every link skill. Otherwise one cumulative list per missing
    /// link, in ascending line order; the last one is the full guild list.
    /// </summary>
    /// <param name="ownSkills">The character's own skills, group to level.</param>
    /// <param name="held">The skill list the client last received (group to level), or null.</param>
    /// <param name="guildSkills">The guild's learned skill ids (line * 100 + level).</param>
    /// <param name="table">Guild_Skill_Level by Guild_Skill_ID.</param>
    public static IReadOnlyList<IReadOnlyList<(uint Group, byte Level)>> PlanGuildLinkBurst(
        IEnumerable<KeyValuePair<uint, byte>> ownSkills, IReadOnlyDictionary<uint, byte>? held,
        IEnumerable<uint> guildSkills, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        if (held is null) return [];
        var own = new Dictionary<uint, byte>();
        foreach (var (group, level) in ownSkills)
            if (level > own.GetValueOrDefault(group)) own[group] = level;

        var links = new List<(uint Group, byte Level)>();
        foreach (var (line, level) in LineLevels(guildSkills).OrderBy(kv => kv.Key))
        {
            if (!table.TryGetValue(line * 100 + (uint)level, out var row) || row.Link_Skill_ID == 0) continue;
            uint group = row.Link_Skill_ID / 100;
            byte need = Math.Max((byte)(row.Link_Skill_ID % 100), own.GetValueOrDefault(group));
            links.Add((group, need));
        }

        var missing = links.Where(l => held.GetValueOrDefault(l.Group) != l.Level).ToList();
        if (missing.Count == 0) return [];

        var current = new Dictionary<uint, byte>(own);
        foreach (var l in links)
            if (!missing.Contains(l)) current[l.Group] = l.Level;

        var burst = new List<IReadOnlyList<(uint Group, byte Level)>>(missing.Count);
        foreach (var (group, level) in missing)
        {
            current[group] = level;
            burst.Add(current.Select(kv => (kv.Key, kv.Value)).ToArray());
        }
        return burst;
    }
}
