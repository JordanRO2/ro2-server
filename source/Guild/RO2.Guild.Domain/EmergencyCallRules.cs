using System.Collections.Frozen;
using System.Text.Json;
using RO2.GameData.Tables;

namespace RO2.Guilds.Domain;

/// <summary>Why a landed Emergency Call issues no call. <see cref="None"/> means it does.</summary>
public enum EmergencyCallRefusal
{
 /// <summary>The call is allowed.</summary>
    None,
 /// <summary>The caster is in no guild.</summary>
    NoGuild,
 /// <summary>The caster is not the guild's master.</summary>
    NotMaster,
 /// <summary>The guild has not learned the Guild_Skill_Level row whose Link_Skill_ID is this skill.</summary>
    NotLearned,
 /// <summary>The caster's map is not in the skill's Targeting_Map_ID.</summary>
    WrongMap,
}

/// <summary>
/// Pure rules of the guild skill "Emergency Call" (Guild_Skill_Base 210501..210503). No disk, host or clock: the
/// parsed Skill_Function-28 groups and the Guild_Skill_Level rows are passed in.
/// </summary>
public static class EmergencyCallRules
{
 /// <summary>Skill_Table_Base.Skill_Function of the Emergency Call: group 40001 is the only row with it.</summary>
    public const int EmergencyCallSkillFunction = 28;

 /// <summary>True when <paramref name="skillId"/> (group*100+level) is an Emergency Call level.</summary>
    public static bool IsEmergencyCallSkill(IReadOnlyDictionary<uint, FrozenSet<uint>> groups, uint skillId)
        => groups.ContainsKey(skillId / 100);

 /// <summary>The maps the skill may be cast on (Skill_Table_Base.Targeting_Map_ID), empty when unknown.</summary>
    public static IReadOnlySet<uint> TargetMapsOf(IReadOnlyDictionary<uint, FrozenSet<uint>> groups, uint skillId)
        => groups.TryGetValue(skillId / 100, out var maps) ? maps : FrozenSet<uint>.Empty;

 /// <summary>
 /// Maps each Skill_Group whose Skill_Function is <see cref="EmergencyCallSkillFunction"/> to its
 /// Targeting_Map_ID set. The shipped table gives 40001 → {3101, 3201}.
 /// </summary>
    public static FrozenDictionary<uint, FrozenSet<uint>> ParseEmergencyCallGroups(ReadOnlySpan<byte> skillTableJson)
    {
        var reader = new Utf8JsonReader(skillTableJson, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        var groups = new Dictionary<uint, FrozenSet<uint>>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Skill_Function", out var fn) && fn.ValueKind == JsonValueKind.Number
                && fn.TryGetInt32(out var function) && function == EmergencyCallSkillFunction
                && row.TryGetProperty("Skill_Group", out var g) && g.TryGetUInt32(out var group))
            {
                var maps = row.TryGetProperty("Targeting_Map_ID", out var m) && m.ValueKind == JsonValueKind.String
                    ? ParseMapList(m.GetString())
                    : FrozenSet<uint>.Empty;
                groups[group] = maps;
            }
        }
        return groups.ToFrozenDictionary();
    }

 /// <summary>Parses a Targeting_Map_ID cell such as <c>"3101,3201,"</c> (comma-separated, trailing comma).</summary>
    public static FrozenSet<uint> ParseMapList(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return FrozenSet<uint>.Empty;
        var maps = new HashSet<uint>();
        foreach (var part in cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out var id))
                maps.Add(id);
        return maps.ToFrozenSet();
    }

 /// <summary>Decides whether a landed Emergency Call by <paramref name="casterCharId"/> issues a call.</summary>
 /// <remarks>
 /// <list type="bullet">
 /// <item>The caster must be the guild master.</item>
 /// <item>The guild must have learned the skill: one of its learned Guild_Skill_Level rows links to this skill.</item>
 /// <item>The caster's map must be in the skill's Targeting_Map_ID.</item>
 /// </list>
 /// </remarks>
    public static EmergencyCallRefusal Validate(Guild? guild, uint casterCharId, uint skillId, uint casterMapId,
        IReadOnlySet<uint> targetMaps, IReadOnlyList<GuildSkillLevel> guildSkillLevels)
    {
        if (guild is null)
            return EmergencyCallRefusal.NoGuild;
        if (guild.MasterCharId != casterCharId)
            return EmergencyCallRefusal.NotMaster;

        uint[] learned;
        lock (guild.Gate) learned = guild.Skills.ToArray();
        var holds = false;
        foreach (var row in guildSkillLevels)
        {
            if (row.Link_Skill_ID == skillId && Array.IndexOf(learned, row.Guild_Skill_ID) >= 0)
            {
                holds = true;
                break;
            }
        }
        if (!holds)
            return EmergencyCallRefusal.NotLearned;

        return targetMaps.Contains(casterMapId) ? EmergencyCallRefusal.None : EmergencyCallRefusal.WrongMap;
    }
}
