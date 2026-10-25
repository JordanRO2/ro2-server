using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild skill "Emergency Call" (Guild_Skill_Base 210501..210503): the guild master summons the other online
/// members to the master's location. Holds the skill rules, the pending call and its answer window.
/// </summary>
/// <remarks>
/// Only the guild master may cast it, on the maps listed in the skill's Targeting_Map_ID. A cast issues a
/// 2960 prompt to every other online member; a member who accepts while the call is open is moved to the
/// master's position. The caster is not prompted. A call stays open for
/// <see cref="EconomyMiscPackets.GuildEmergencyCallAnswerSeconds"/>. State is process-wide and static, so no
/// DI registration is needed.
/// </remarks>
public static class GuildEmergencyCallService
{
 /// <summary>Skill_Table_Base.Skill_Function of the Emergency Call: group 40001 is the only row with it.</summary>
    internal const int EmergencyCallSkillFunction = 28;

 /// <summary>The file GameDataStore loads Skill_Table_Base from (same relative "data" directory).</summary>
    private static readonly string ShippedSkillTable = Path.Combine("data", "Skill_Table_Base.json");

    private static readonly Lazy<(FrozenDictionary<uint, FrozenSet<uint>> Groups, string? Error)> Shipped =
        new(LoadShippedGroups);

 /// <summary>True when <paramref name="skillId"/> (group*100+level) is an Emergency Call level.</summary>
 /// <remarks>
 /// The rows are read from the shipped Skill_Table_Base data, not from a list in code.
 /// </remarks>
    public static bool IsEmergencyCallSkill(uint skillId) => Shipped.Value.Groups.ContainsKey(skillId / 100);

 /// <summary>Why the Emergency Call rows could not be read, or null when they were.</summary>
    public static string? SkillTableError => Shipped.Value.Error;

 /// <summary>The maps the skill may be cast on (Skill_Table_Base.Targeting_Map_ID), empty when unknown.</summary>
    public static IReadOnlySet<uint> TargetMapsOf(uint skillId)
        => Shipped.Value.Groups.TryGetValue(skillId / 100, out var maps) ? maps : FrozenSet<uint>.Empty;

 /// <summary>
 /// Maps each Skill_Group whose Skill_Function is <see cref="EmergencyCallSkillFunction"/> to its
 /// Targeting_Map_ID set. The shipped table gives 40001 → {3101, 3201}.
 /// </summary>
    internal static FrozenDictionary<uint, FrozenSet<uint>> ParseEmergencyCallGroups(ReadOnlySpan<byte> skillTableJson)
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
    internal static FrozenSet<uint> ParseMapList(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            return FrozenSet<uint>.Empty;
        var maps = new HashSet<uint>();
        foreach (var part in cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out var id))
                maps.Add(id);
        return maps.ToFrozenSet();
    }

    private static (FrozenDictionary<uint, FrozenSet<uint>>, string?) LoadShippedGroups()
    {
        try
        {
            var groups = ParseEmergencyCallGroups(File.ReadAllBytes(ShippedSkillTable));
            return groups.Count > 0
                ? (groups, null)
                : (groups, $"{ShippedSkillTable} has no Skill_Function {EmergencyCallSkillFunction} row");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (FrozenDictionary<uint, FrozenSet<uint>>.Empty, $"{ShippedSkillTable} could not be read: {ex.Message}");
        }
    }

    
    
    
 /// <summary>Why a landed Emergency Call issues no call. <see cref="None"/> means it does.</summary>
    public enum CastRefusal
    {
        None,
 /// <summary>The caster is in no guild.</summary>
        NoGuild,
 /// <summary>The caster is not the guild's master (see the class remarks).</summary>
        NotMaster,
 /// <summary>The guild has not learned the Guild_Skill_Level row whose Link_Skill_ID is this skill.</summary>
        NotLearned,
 /// <summary>The caster's map is not in the skill's Targeting_Map_ID.</summary>
        WrongMap,
    }

 /// <summary>Decides whether a landed Emergency Call by <paramref name="casterCharId"/> issues a call.</summary>
 /// <remarks>
 /// <list type="bullet">
 /// <item>The caster must be the guild master.</item>
 /// <item>The guild must have learned the skill: one of its learned Guild_Skill_Level rows links to this skill.</item>
 /// <item>The caster's map must be in the skill's Targeting_Map_ID.</item>
 /// </list>
 /// </remarks>
    internal static CastRefusal Validate(Guild? guild, uint casterCharId, uint skillId, uint casterMapId,
        IReadOnlySet<uint> targetMaps, IReadOnlyList<GuildSkillLevel> guildSkillLevels)
    {
        if (guild is null)
            return CastRefusal.NoGuild;
        if (guild.MasterCharId != casterCharId)
            return CastRefusal.NotMaster;

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
            return CastRefusal.NotLearned;

        return targetMaps.Contains(casterMapId) ? CastRefusal.None : CastRefusal.WrongMap;
    }

    
    
    
 /// <summary>
 /// One issued call: where the members go, until when, and which members were prompted and have not
 /// answered yet.
 /// </summary>
    public sealed class PendingCall
    {
        private readonly HashSet<uint> _awaiting;

        internal PendingCall(uint guildId, uint casterCharId, uint mapId, float x, float y, float z,
            DateTime issuedUtc, IEnumerable<uint> promptedCharIds)
        {
            GuildId = guildId;
            CasterCharId = casterCharId;
            MapId = mapId;
            X = x;
            Y = y;
            Z = z;
            IssuedUtc = issuedUtc;
            ExpiresUtc = issuedUtc.AddSeconds(EconomyMiscPackets.GuildEmergencyCallAnswerSeconds);
            _awaiting = new HashSet<uint>(promptedCharIds);
        }

        public uint GuildId { get; }
        public uint CasterCharId { get; }
 /// <summary>The master's map at the landing.</summary>
        public uint MapId { get; }
 /// <summary>The master's position at the landing; see <see cref="IssueAsync"/> for its source.</summary>
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public DateTime IssuedUtc { get; }
        public DateTime ExpiresUtc { get; }

 /// <summary>Removes <paramref name="charId"/> from the members still expected to answer.</summary>
        internal bool TryTake(uint charId)
        {
            lock (_awaiting) return _awaiting.Remove(charId);
        }
    }

 /// <summary>guild id → its latest call. A new call replaces the old one.</summary>
    private static readonly ConcurrentDictionary<uint, PendingCall> Calls = new();

 /// <summary>
 /// Records a call before its prompts go out, so a 1653 that arrives right behind a 2960 always finds it.
 /// </summary>
    internal static PendingCall Record(uint guildId, uint casterCharId, uint mapId, float x, float y, float z,
        DateTime issuedUtc, IEnumerable<uint> promptedCharIds)
    {
        var call = new PendingCall(guildId, casterCharId, mapId, x, y, z, issuedUtc, promptedCharIds);
        Calls[guildId] = call;
        return call;
    }

 /// <summary>
 /// Takes <paramref name="charId"/>'s answer to its guild's call: true, with the call, when that member
 /// was prompted, has not answered yet and the window is still open.
 /// </summary>
 /// <remarks>
 /// An answer is accepted once per member and call, and only while the call's window is open.
 /// </remarks>
    public static bool TryConsume(uint guildId, uint charId, DateTime nowUtc, out PendingCall call)
    {
        call = null!;
        if (!Calls.TryGetValue(guildId, out var found))
            return false;
        if (nowUtc > found.ExpiresUtc)
        {
            Calls.TryRemove(new KeyValuePair<uint, PendingCall>(guildId, found));
            return false;
        }
        if (!found.TryTake(charId))
            return false;
        call = found;
        return true;
    }

 /// <summary>
 /// Issues a call for <paramref name="guild"/>: records it at the caster's map and position and sends the
 /// 2960 prompt to every other member who is online. Returns how many members were prompted.
 /// </summary>
 /// <remarks>
 /// Recipients are the online members other than the caster. The call is recorded at the caster's map and
 /// position before the prompts are sent, so an answer arriving right after a prompt is always matched.
 /// </remarks>
    public static async Task<int> IssueAsync(Guild guild, PlayerState caster, PlayerStateManager players,
        BroadcastService broadcast, DateTime nowUtc)
    {
        uint[] memberIds;
        lock (guild.Gate) memberIds = guild.Members.Select(m => m.CharId).ToArray();

        var recipients = new List<(uint CharId, ProudSession Session)>();
        foreach (var charId in memberIds)
        {
            if (charId == caster.CharacterId)
                continue;
            var member = players.GetPlayerByCharacterId(charId);
            if (member is null)
                continue;
            var session = broadcast.GetSession(member.SessionId);
            if (session is null || !session.IsConnected)
                continue;
            recipients.Add((charId, session));
        }
        if (recipients.Count == 0)
            return 0;

        Record(guild.Id, caster.CharacterId, caster.MapId, caster.X, caster.Y, caster.Z, nowUtc,
            recipients.Select(r => r.CharId));

        var body = EconomyMiscPackets.BuildGuildEmergencyCallAsk();
        foreach (var (_, session) in recipients)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsGuildEmergencyCallAsk, body);
        return recipients.Count;
    }
}
