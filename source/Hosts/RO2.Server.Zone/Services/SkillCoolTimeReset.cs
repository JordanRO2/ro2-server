using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text.Json;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2817 NfyCoolTimeClear: ends a skill group's cooldown early, on a refused 1509 or a skill that
/// resets every cooldown.
/// </summary>
public static class SkillCoolTimeReset
{
    /// <summary>
    /// A Skill_Group is a skill id without its two trailing level digits: <c>skillId / 100</c>
    /// (skill 410101 belongs to group 4101).
    /// </summary>
    private const uint SkillIdsPerGroup = 100;

    /// <summary>Skill_Level of the row whose description names a reset-all skill.</summary>
    private const int FirstSkillLevel = 1;

    /// <summary>Skill_Table_Base.Check_Learn_Skill value of a skill the player learns from the skill window.</summary>
    private const int CheckLearnSkillLearnable = 1;

    /// <summary>Talent_Table.ISJobMaster value of a Ymir Child (job master) skill.</summary>
    private const int JobMasterSkill = 1;

    private static uint SkillGroupOf(uint skillId) => skillId / SkillIdsPerGroup;

    /// <summary>
    /// 2817 is a fixed 4-byte body, <c>[u32 LE skill group]</c>. The group must be above 0 (0 would clear
    /// every group) and the message is unicast to the caster, since it names no actor.
    /// </summary>
    internal const int CoolTimeClearBodyLen = sizeof(uint);

 /// <summary>
 /// Builds the 2817 body for <paramref name="skillGroup"/> (a Skill_Group, i.e. skillId / 100).
 /// </summary>
    public static byte[] BuildCoolTimeClear2817(uint skillGroup)
    {
        var body = new byte[CoolTimeClearBodyLen];
        BinaryPrimitives.WriteUInt32LittleEndian(body, skillGroup);
        return body;
    }

    
    
    
 /// <summary>
 /// The refund that goes out in front of a 24310 refusal: when the refused skill's group is still
 /// cooling, sends its 2817 and ends the server-side cooldown. Returns whether it did.
 /// </summary>
    public static async Task<bool> RefundOnRefusalAsync(ProudSession session, PlayerState player, uint skillId,
        DateTime nowUtc)
    {
        var group = SkillGroupOf(skillId);
        if (!TryEndCooldown(player.SkillCooldowns, group, nowUtc))
            return false;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyCoolTimeClear, BuildCoolTimeClear2817(group));
        return true;
    }

 /// <summary>
 /// Removes <paramref name="group"/> from <paramref name="cooldowns"/> when it is still cooling.
 /// </summary>
    internal static bool TryEndCooldown(Dictionary<uint, DateTime> cooldowns, uint group, DateTime nowUtc)
    {
        if (!cooldowns.TryGetValue(group, out var expiry) || expiry <= nowUtc)
            return false;
        cooldowns.Remove(group);
        return true;
    }

    
    
    
 /// <summary>
 /// The phrase the level-1 description of a reset-all skill carries in the shipped
 /// String_Skill_Description. Three groups have it: 3230 True Sight (620323001, "Resets the Cool
 /// Time for all of your skills."), 4312 Dirty Plan (620431201) and 5304 (620530401).
 /// </summary>
    internal const string ResetAllPhrase = "resets the cool time for all of your skills";

    private static readonly string ShippedSkillBase = Path.Combine("data", "Skill_Table_Base.json");
    private static readonly string ShippedSkillLevel = Path.Combine("data", "Skill_Table_SkillLevel.json");
    private static readonly string ShippedTalent = Path.Combine("data", "Talent_Table.json");
    private static readonly string ShippedDescriptions = Path.Combine("data", "strings", "String_Skill_Description.json");

    private static readonly Lazy<(FrozenSet<uint> ResetAll, FrozenSet<uint> Resettable, string? Error)> Shipped =
        new(LoadShipped);

 /// <summary>True when <paramref name="skillId"/> belongs to a group that resets every cooldown.</summary>
    public static bool IsResetAllSkill(uint skillId) => Shipped.Value.ResetAll.Contains(SkillGroupOf(skillId));

 /// <summary>Why the reset rule could not be read from the shipped data, or null when it was.</summary>
    public static string? TableError => Shipped.Value.Error;

 /// <summary>
 /// On the landing of a reset-all skill, sends one 2817 per group of <paramref name="player"/> that
 /// is still cooling and resettable (see <see cref="TakeResettableGroups"/>), and ends those
 /// cooldowns. Returns how many went out.
 /// </summary>
 /// <remarks>
 /// Each 2817 is sent between the 2492 and the 2718 of the cast. The order inside a burst is not fixed, so ascending is used.
 /// </remarks>
    public static async Task<int> ResetAllAsync(ProudSession session, PlayerState player, uint skillId,
        DateTime nowUtc)
    {
        var resettable = Shipped.Value.Resettable;
        var groups = TakeResettableGroups(player.SkillCooldowns, SkillGroupOf(skillId), nowUtc, resettable.Contains);
        foreach (var group in groups)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyCoolTimeClear, BuildCoolTimeClear2817(group));
        return groups.Count;
    }

 /// <summary>
 /// The groups a reset-all cast clears, in ascending order, removed from <paramref
 /// name="cooldowns"/>: every group still cooling, other than the cast's own, that <paramref
 /// name="isResettable"/> accepts.
 /// </summary>
 /// <remarks>
 /// Each of the other 10 differs by one group. What that keeps: item skills (20xxx), potion and warp
 /// skills (9xxxx) and 40005, and the Ymir Child skill 4300.
 /// </remarks>
    internal static List<uint> TakeResettableGroups(Dictionary<uint, DateTime> cooldowns, uint castGroup,
        DateTime nowUtc, Func<uint, bool> isResettable)
    {
        var groups = new List<uint>();
        foreach (var (group, expiry) in cooldowns)
            if (group != castGroup && expiry > nowUtc && isResettable(group))
                groups.Add(group);
        groups.Sort();
        foreach (var group in groups)
            cooldowns.Remove(group);
        return groups;
    }

 /// <summary>
 /// The Skill_Group of every level-1 Skill_Table_SkillLevel row whose String_Skill_Description text
 /// contains <see cref="ResetAllPhrase"/>. The shipped data gives {3230, 4312, 5304}.
 /// </summary>
    internal static FrozenSet<uint> ParseResetAllGroups(ReadOnlySpan<byte> skillLevelJson, ReadOnlySpan<byte> descriptionJson)
    {
        var texts = new Dictionary<string, string>();
        using (var strings = JsonDocument.Parse(descriptionJson.ToArray()))
        {
            foreach (var entry in strings.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String)
                    texts[entry.Name] = entry.Value.GetString() ?? string.Empty;
        }
        using var doc = JsonDocument.Parse(skillLevelJson.ToArray());
        var groups = new HashSet<uint>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Skill_Level", out var lv) && lv.TryGetInt32(out var level) && level == FirstSkillLevel
                && row.TryGetProperty("Skill_Group", out var g) && g.TryGetUInt32(out var group)
                && row.TryGetProperty("String_Skill_Description", out var d) && d.TryGetUInt64(out var textId)
                && texts.TryGetValue(textId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var text)
                && text.Contains(ResetAllPhrase, StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(group);
            }
        }
        return groups.ToFrozenSet();
    }

 /// <summary>
 /// The groups a reset-all skill may clear: Skill_Table_Base.Check_Learn_Skill 1, minus the groups
 /// whose Talent_Table row is ISJobMaster 1 (the Ymir Child skills 1200..5300 and 3322).
 /// </summary>
    internal static FrozenSet<uint> ParseResettableGroups(ReadOnlySpan<byte> skillBaseJson, ReadOnlySpan<byte> talentJson)
    {
        var jobMaster = new HashSet<uint>();
        using (var talent = JsonDocument.Parse(talentJson.ToArray()))
        {
            foreach (var row in talent.RootElement.EnumerateArray())
                if (row.TryGetProperty("ISJobMaster", out var m) && m.TryGetInt32(out var isMaster) && isMaster == JobMasterSkill
                    && row.TryGetProperty("Skill_Group_ID", out var g) && g.TryGetUInt32(out var group))
                    jobMaster.Add(group);
        }

        var groups = new HashSet<uint>();
        using var skills = JsonDocument.Parse(skillBaseJson.ToArray());
        foreach (var row in skills.RootElement.EnumerateArray())
            if (row.TryGetProperty("Check_Learn_Skill", out var c) && c.TryGetInt32(out var learn) && learn == CheckLearnSkillLearnable
                && row.TryGetProperty("Skill_Group", out var g) && g.TryGetUInt32(out var group)
                && !jobMaster.Contains(group))
                groups.Add(group);
        return groups.ToFrozenSet();
    }

    private static (FrozenSet<uint>, FrozenSet<uint>, string?) LoadShipped()
    {
        try
        {
            var resetAll = ParseResetAllGroups(File.ReadAllBytes(ShippedSkillLevel), File.ReadAllBytes(ShippedDescriptions));
            var resettable = ParseResettableGroups(File.ReadAllBytes(ShippedSkillBase), File.ReadAllBytes(ShippedTalent));
            return resetAll.Count > 0
                ? (resetAll, resettable, null)
                : (resetAll, resettable, $"{ShippedDescriptions} names no skill that resets every cooldown");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenSet<uint>.Empty, FrozenSet<uint>.Empty,
                $"the cooldown-reset tables could not be read: {ex.Message}");
        }
    }
}
