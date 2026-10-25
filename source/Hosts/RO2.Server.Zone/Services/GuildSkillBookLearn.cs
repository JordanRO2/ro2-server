using System.Buffers.Binary;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild skill books: handles 1652 ReqGuildSkillLearnBook, sent when the guild master uses a skill book, and
/// builds the 2959 AnsGuildSkillLearnBook answer.
/// </summary>
/// <remarks>
/// <para>
/// A book teaches its Guild_Skill_ID to the whole guild. Only the first level of a skill line is learned from
/// a book; a higher level requires the previous one to be held. Book skills take no guild skill points.
/// </para>
/// <para>
/// Request body: <c>[u64 item uid]</c>. Answer: <c>[u16 result]</c>, where 0 means learned, 0x31 means the
/// previous level is missing, and 70 means the level is already held.
/// </para>
/// </remarks>
internal static class GuildSkillBookLearn
{
 /// <summary>Item_Type of guild skill books in the ItemInfo data table.</summary>
    internal const byte GuildSkillBookItemType = 113;

 /// <summary>Skill_LearnType of the guild skill lines that are learned from books.</summary>
    internal const byte BookLearnType = 1;

 /// <summary>Length in bytes of a 1652 request body: the u64 item uid.</summary>
    internal const int RequestBodyLength = 8;

 /// <summary>Length in bytes of a 2959 answer body: the u16 result.</summary>
    internal const int AnswerBodyLength = 2;

 /// <summary>
 /// Guild_Skill_ID is <c>line * SkillIdLineStride + level</c>; a player skill id (Link_Skill_ID) is
 /// <c>group * SkillIdLineStride + level</c> in the same way.
 /// </summary>
    internal const uint SkillIdLineStride = 100;

 /// <summary>2959 result 0: the book was learned.</summary>
    internal const ushort ResultLearned = 0;

 /// <summary>2959 result 0x31: the previous level of the line is not held.</summary>
    internal const ushort ResultPreviousLevelMissing = 0x31;

 /// <summary>2959 result 70: the level is already held.</summary>
    internal const ushort ResultAlreadyLearned = 70;

 /// <summary>Outcome of using a book for a guild.</summary>
    internal enum Verdict
    {
 /// <summary>The book's skill is learned.</summary>
        Learn,
 /// <summary>The guild already holds that level of the line or a higher one.</summary>
        AlreadyLearned,
 /// <summary>The previous level of the line is not held.</summary>
        PreviousLevelMissing,
 /// <summary>
 /// The book does not teach a skill from this path, or the guild level is below the required level. Nothing
 /// is answered.
 /// </summary>
        NotLearnable,
    }

 /// <summary>
 /// Decides what a book skill does for a guild of the given level holding the given learned skills.
 /// </summary>
 /// <param name="skillId">The book's Skill_ID_1 from the ItemInfo data table.</param>
 /// <param name="learned">Skill ids the guild has learned.</param>
 /// <param name="guildLevel">The guild's level.</param>
    internal static Verdict Plan(uint skillId, IEnumerable<uint> learned, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        if (skillId == 0 || !table.TryGetValue(skillId, out var row) || row.Skill_LearnType != BookLearnType)
            return Verdict.NotLearnable;
        if (guildLevel < row.Need_GuildLevel) return Verdict.NotLearnable;

        var levels = GuildSkillLearnPackets.LineLevels(learned);
        bool Holds(uint id) => id == 0
            || levels.GetValueOrDefault(GuildSkillLearnPackets.LineOf(id)) >= GuildSkillLearnPackets.LevelOf(id);

        if (Holds(skillId)) return Verdict.AlreadyLearned;
        if (!Holds(row.Need_Skill_ID_1) || !Holds(row.Need_Skill_ID_2)) return Verdict.PreviousLevelMissing;
        return Verdict.Learn;
    }

 /// <summary>The 2959 result for a verdict.</summary>
    internal static ushort ResultOf(Verdict verdict) => verdict switch
    {
        Verdict.Learn => ResultLearned,
        Verdict.AlreadyLearned => ResultAlreadyLearned,
        Verdict.PreviousLevelMissing => ResultPreviousLevelMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not answered"),
    };

 /// <summary>
 /// Builds a member's 2488 skill list: its own skills plus the link skill of the highest level the guild holds
 /// on each guild skill line. Each entry is a (group, level) pair, with id = group * 100 + level. When a group
 /// appears twice, the higher level is kept.
 /// </summary>
    internal static IReadOnlyList<(uint Group, byte Level)> SkillListWithGuildLinks(
        IEnumerable<KeyValuePair<uint, byte>> ownSkills, IEnumerable<uint> guildSkills,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        var byGroup = new Dictionary<uint, byte>();
        void Add(uint group, byte level)
        {
            if (level > byGroup.GetValueOrDefault(group)) byGroup[group] = level;
        }
        foreach (var (group, level) in ownSkills) Add(group, level);
        foreach (var (line, level) in GuildSkillLearnPackets.LineLevels(guildSkills))
            if (table.TryGetValue(line * SkillIdLineStride + (uint)level, out var row) && row.Link_Skill_ID != 0)
                Add(GuildSkillLearnPackets.LineOf(row.Link_Skill_ID), (byte)GuildSkillLearnPackets.LevelOf(row.Link_Skill_ID));
        return byGroup.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

 /// <summary>
 /// Builds the 2488 list for a player: its own skills plus its guild's line skills, or only its own skills when
 /// it is in no guild. Every 2488 sender must use this, since a 2488 replaces the client's whole list.
 /// </summary>
    internal static IReadOnlyList<(uint Group, byte Level)> SkillListFor(PlayerState player, GameDataStore data)
    {
        uint[] guildSkills = [];
        if (GuildService.Instance.GetByCharId(player.CharacterId) is { } guild)
            lock (guild.Gate) guildSkills = guild.Skills.ToArray();
        return SkillListWithGuildLinks(player.LearnedSkills, guildSkills, GuildSkillLearnPackets.Index(data.GuildSkillLevel));
    }

 /// <summary>Reads a 1652 request: exactly 8 bytes, the book's item uid.</summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out ulong itemUid)
    {
        itemUid = body.Length == RequestBodyLength ? BinaryPrimitives.ReadUInt64LittleEndian(body) : 0;
        return body.Length == RequestBodyLength;
    }

 /// <summary>Builds a 2959 AnsGuildSkillLearnBook body: <c>[u16 result]</c>.</summary>
    internal static byte[] BuildAnsGuildSkillLearnBook2959(ushort result)
    {
        var body = new byte[AnswerBodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }
}
