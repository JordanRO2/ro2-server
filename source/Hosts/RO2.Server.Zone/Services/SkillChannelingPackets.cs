using System.Buffers.Binary;
using System.Text.Json;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 2897 OnSkillChannelingCompleteACK, sent in answer to the caster's 1609
/// ReqSkillChannelingComplete, and the lookup that decides when a cast opens a channel.
/// </summary>
internal static class SkillChannelingPackets
{
 /// <summary>2897 result: the channel was still active and completes.</summary>
    internal const ushort ChannelCompleted = 0;

 /// <summary>2897 result: the server no longer holds that channel; the client ends the cast.</summary>
    internal const ushort ChannelNotActive = 1;

 /// <summary>2897 body: <c>[u32 skillId][u16 result]</c>, 6 bytes.</summary>
    internal static byte[] BuildSkillChannelingCompleteAck2897(uint skillId, ushort result)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, skillId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), result);
        return body;
    }

 /// <summary>
 /// The Skill_Table_SkillLevel.json column this needs; GameDataStore's SkillLevelData does not load
 /// it.
 /// </summary>
    private sealed record SkillDurationRow(uint ID, float Skill_Duration);

    private static readonly Lazy<IReadOnlySet<uint>?> Channelled =
        new(() => LoadChannelledSkills(Path.Combine("data", "Skill_Table_SkillLevel.json")));

 /// <summary>
 /// True when a cast of <paramref name="skillId"/> opens a channel, i.e. its Skill_Table_SkillLevel
 /// row has <c>Skill_Duration</c> &gt; 0. With the table unreadable, every skill is treated as one,
 /// so a 1609 is still answered 0 for the last skill cast.
 /// </summary>
 /// <remarks>11 rows carry a duration: the ten levels of group 6304 (5.0 s) and 7372101.</remarks>
    internal static bool IsChannelled(uint skillId) => Channelled.Value is not { } set || set.Contains(skillId);

 /// <summary>
 /// The two Skill_Table_Base.json columns the move-cancel rule needs; GameDataStore's SkillData does
 /// not load <c>Is_Movable</c>.
 /// </summary>
    private sealed record SkillMovableRow(uint Skill_Group, int Is_Movable);

    private static readonly Lazy<IReadOnlySet<uint>?> ImmovableGroups =
        new(() => LoadImmovableSkillGroups(Path.Combine("data", "Skill_Table_Base.json")));

 /// <summary>
 /// True when moving ends a channel of <paramref name="skillId"/>: it is a channelled skill (<see
 /// cref="IsChannelled"/>) and its Skill_Table_Base group (<c>skillId / 100</c>) has
 /// <c>Is_Movable</c> 0. With either table unreadable it is false, so no cancel is ever made up.
 /// </summary>
 /// <remarks>
 /// Of the 483 2585s with an own 1526 before them in those windows, 397 follow a move by that rule,
 /// at a median of 0.079 s (max 0.39 s). Both channelled groups, 6304 and 73721 (7372101), have
 /// <c>Is_Movable</c> 0, <c>Is_Casting_Cancel</c> 1 and <c>Be_Attacked_Cancel</c> 0 in
 /// Skill_Table_Base. Not modelled: the result-1 answers with no move before them.
 /// </remarks>
    internal static bool CancelsOnMove(uint skillId)
        => skillId != 0 && Channelled.Value is { } channelled && channelled.Contains(skillId)
           && ImmovableGroups.Value is { } immovable && immovable.Contains(skillId / 100);

 /// <summary>
 /// The <c>Skill_Group</c>s whose <c>Is_Movable</c> is 0 in the given Skill_Table_Base.json, or null
 /// when the file is missing or unreadable.
 /// </summary>
    internal static IReadOnlySet<uint>? LoadImmovableSkillGroups(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var rows = JsonSerializer.Deserialize<List<SkillMovableRow>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            return rows.Where(r => r.Is_Movable == 0).Select(r => r.Skill_Group).ToHashSet();
        }
        catch
        {
            return null;
        }
    }

 /// <summary>
 /// The ids whose <c>Skill_Duration</c> is above 0 in the given Skill_Table_SkillLevel.json, or null
 /// when the file is missing or unreadable.
 /// </summary>
    internal static IReadOnlySet<uint>? LoadChannelledSkills(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var rows = JsonSerializer.Deserialize<List<SkillDurationRow>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            return rows.Where(r => r.Skill_Duration > 0f).Select(r => r.ID).ToHashSet();
        }
        catch
        {
            return null;
        }
    }
}
