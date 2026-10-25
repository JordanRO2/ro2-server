using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild skill learning: handles 1512 ReqGuildSkillLearn, decides what the request learns, computes the guild's
/// unspent skill points, and builds the 2724 NfyGuildSkillLearn notice sent to the members.
/// </summary>
/// <remarks>
/// <para>
/// Request body: <c>[u32 len = 2 + 4n][u16 n][n x u32 Guild_Skill_ID]</c>. Each id is the line times 100 plus the
/// level. The request is refused as a whole when any id cannot be learned, and nothing is learned.
/// </para>
/// <para>
/// Notice body: <c>[u32 innerLen = 3 + 4n][u8 unspent points after the learn][u16 n][n x u32 id]</c>. The
/// notice lists the highest level reached on each line.
/// </para>
/// <para>
/// Unspent points are the guild level minus one, less one per level held on a point-cost line. The guild panel
/// must carry the same value, or the client cannot send a learn request.
/// </para>
/// </remarks>
internal static class GuildSkillLearnPackets
{
    
    
    
    
 /// <summary>2722 result 0: learned. The client waits for the notice to update its window.</summary>
    internal const ushort ResultLearned = 0;

 /// <summary>2722 result 7: no guild.</summary>
    internal const ushort ResultNoGuild = 7;

 /// <summary>
 /// 2722 result 19: only the guild master may learn guild skills. Checked before any other rule.
 /// </summary>
    internal const ushort ResultNotMaster = 19;

 /// <summary>2722 result 47: no empty skill points.</summary>
    internal const ushort ResultEmptySkillPoint = 47;

 /// <summary>2722 result 48: wrong skill index.</summary>
    internal const ushort ResultWrongSkillIndex = 48;

 /// <summary>2722 result 49: the first required skill is missing.</summary>
    internal const ushort ResultNotHaveNeedSkill1 = 49;

 /// <summary>2722 result 50: the second required skill is missing.</summary>
    internal const ushort ResultNotHaveNeedSkill2 = 50;

 /// <summary>Why a learn request was refused by the rules.</summary>
    internal enum LearnRefusal
    {
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

 /// <summary>Returns the 2722 result code for a refusal. Zero for no refusal.</summary>
    internal static ushort ResultCodeOf(LearnRefusal refusal) => refusal switch
    {
        LearnRefusal.None => ResultLearned,
        LearnRefusal.NotEnoughPoints => ResultEmptySkillPoint,
        LearnRefusal.MissingNeedSkill1 => ResultNotHaveNeedSkill1,
        LearnRefusal.MissingNeedSkill2 => ResultNotHaveNeedSkill2,
        _ => ResultWrongSkillIndex,
    };

 /// <summary>
 /// Returns the gate result checked before any rule: 7 when there is no guild, 19 when the character is not the
 /// guild master, and 0 otherwise.
 /// </summary>
    internal static ushort GateResult(Guild? guild, uint characterId)
        => guild is null ? ResultNoGuild : guild.MasterCharId != characterId ? ResultNotMaster : ResultLearned;

 /// <summary>
 /// Skill_LearnType of the lines bought with guild skill points, the only lines the learn window sells.
 /// </summary>
    internal const byte PointLearnType = 0;

    private static readonly ConditionalWeakTable<IReadOnlyList<GuildSkillLevel>, IReadOnlyDictionary<uint, GuildSkillLevel>> ById = new();

 /// <summary>Indexes the skill rows by Guild_Skill_ID, the first row of each id.</summary>
    internal static IReadOnlyDictionary<uint, GuildSkillLevel> Index(IReadOnlyList<GuildSkillLevel> rows)
        => ById.GetValue(rows, list =>
        {
            var byId = new Dictionary<uint, GuildSkillLevel>(list.Count);
            foreach (var r in list) byId.TryAdd(r.Guild_Skill_ID, r);
            return byId;
        });

 /// <summary>Returns the line of a Guild_Skill_ID, which is the id without its level.</summary>
    internal static uint LineOf(uint skillId) => skillId / 100;

 /// <summary>Returns the level of a Guild_Skill_ID within its line.</summary>
    internal static int LevelOf(uint skillId) => (int)(skillId % 100);

 /// <summary>
 /// Reads a 1512 body. Returns false when the length fields do not match the body.
 /// </summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out uint[] skillIds)
    {
        skillIds = [];
        if (body.Length < 6) return false;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(body);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (len != 2 + 4u * (uint)n || body.Length < 4 + (long)len) return false;
        var ids = new uint[n];
        for (int i = 0; i < n; i++)
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(body[(6 + 4 * i)..]);
        skillIds = ids;
        return true;
    }

 /// <summary>Returns the highest level held on each line, from a guild's learned ids.</summary>
    internal static Dictionary<uint, int> LineLevels(IEnumerable<uint> learned)
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
    internal static byte UnspentPoints(byte guildLevel, IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        var spent = 0;
        foreach (var (line, level) in LineLevels(learned))
            if (table.TryGetValue(line * 100 + (uint)level, out var row) && row.Skill_LearnType == PointLearnType)
                spent += level;
        return (byte)Math.Clamp(guildLevel - 1 - spent, 0, byte.MaxValue);
    }

 /// <summary>
 /// Returns the ids a learn request learns, in an order that satisfies their prerequisites, or null when any of
 /// them cannot be learned. Nothing is learned from a refused request.
 /// </summary>
    internal static List<uint>? PlanLearn(IReadOnlyList<uint> requested, IEnumerable<uint> learned, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
        => PlanLearn(requested, learned, guildLevel, table, out _);

 /// <summary>
 /// Returns the ids a learn request learns, or null when refused, and the reason for the refusal through
 /// <paramref name="refusal"/>.
 /// </summary>
    internal static List<uint>? PlanLearn(IReadOnlyList<uint> requested, IEnumerable<uint> learned, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table, out LearnRefusal refusal)
    {
        refusal = LearnRefusal.WrongSkillIndex;
        if (requested.Count == 0) return null;
        var held = learned.ToArray();
        
        if (requested.Count > UnspentPoints(guildLevel, held, table))
        {
            refusal = LearnRefusal.NotEnoughPoints;
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
            refusal = LearnRefusal.None;
            return accepted;
        }

        
        var stuck = pending[0];
        var stuckRow = table[stuck];
        refusal = LevelOf(stuck) != levels.GetValueOrDefault(LineOf(stuck)) + 1 ? LearnRefusal.WrongSkillIndex
            : !Holds(stuckRow.Need_Skill_ID_1) ? LearnRefusal.MissingNeedSkill1
            : LearnRefusal.MissingNeedSkill2;
        return null;
    }

 /// <summary>
 /// Returns the highest level reached on each line bought by <paramref name="learnedNow"/>, in the order the
 /// lines were first learned. These are the ids the notice names.
 /// </summary>
    internal static List<uint> TopIdPerLine(IEnumerable<uint> learnedNow)
    {
        var order = new List<uint>();
        var top = new Dictionary<uint, uint>();
        foreach (var id in learnedNow)
        {
            var line = LineOf(id);
            if (!top.TryGetValue(line, out var cur)) { order.Add(line); top[line] = id; }
            else if (LevelOf(id) > LevelOf(cur)) top[line] = id;
        }
        return order.Select(l => top[l]).ToList();
    }

 /// <summary>Builds the 2724 NfyGuildSkillLearn body: <c>[u32 innerLen = 3 + 4n][u8 unspentPoints][u16 n][n x u32]</c>.</summary>
    internal static byte[] BuildNfyGuildSkillLearn2724(byte unspentPoints, IReadOnlyList<uint> skillIds)
    {
        using var w = new PacketWriter(4 + 3 + 4 * skillIds.Count);
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteByte(unspentPoints);
        w.WriteUInt16((ushort)skillIds.Count);
        foreach (var id in skillIds) w.WriteUInt32(id);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }
}
