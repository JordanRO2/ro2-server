using System.Buffers.Binary;
using System.Text;
using RO2.Application.Interfaces;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Guild roster (2560, 2573) and guild panel (2559) bodies, built per member and per guild from live server
/// state.
/// </summary>
public static class GuildRosterPackets
{
 /// <summary>
 /// One member as the roster shows them. Level and map come from the live player when the member is online.
 /// Class, profession, job level and ProJob come from the member's character record, not from the copy stored
 /// at join time.
 /// </summary>
 /// <param name="Class">Combat class.</param>
 /// <param name="Profession">Creation profession.</param>
 /// <param name="ProJobLevel">ProJob level.</param>
 /// <param name="ProJobId">ProJob id.</param>
    public readonly record struct MemberView(GuildMember Member, byte Level, byte Class, byte Profession, byte JobLevel,
        byte ProJobLevel, byte ProJobId, uint MapId);

    /// <summary>Length of a GUID in the roster and guild bodies.</summary>
    private const int GuidLen = 16;

    /// <summary>Length of a member record.</summary>
    internal const int MemberRecordLen = 115;

    // Member record layout (offsets within the 115-byte record). Bytes not listed are zero.
    private const int RecGidOff = 0;            // u64 GID
    private const int RecLevelOff = 21;         // u8 level
    private const int RecClassOff = 23;         // u8 combat class
    private const int RecProfessionOff = 25;    // u8 creation profession (ProJob type id 1..4)
    private const int RecJobLevelOff = 27;      // u8 job level
    private const int RecBribePercentOff = 29;  // u8 guild bribe percent
    private const int RecContribOff = 30;       // u32 contributed guild EXP
    private const int RecJoinDateOff = 38;      // 16-byte OLE date: the join date
    private const int RecAuthIdOff = 54;        // u8 rank index, 0 is the master
    private const int RecProJobLevelOff = 55;   // u8 ProJob level
    private const int RecProJobIdOff = 56;      // u8 ProJob id
    private const int RecMapIdOff = 57;         // u32 current map id, 0 when offline
    private const int RecNameOff = 73;          // UTF-16LE name, NUL-terminated
    private const int RecNameMaxChars = (MemberRecordLen - RecNameOff - 2) / 2;   // 20, keeps a terminator

    /// <summary>Length of an OLE date block: <c>{f64 date, u32 status, u32 pad}</c>.</summary>
    private const int OleDateLen = 16;

    /// <summary>Offset of the u32 status within an OLE date block.</summary>
    private const int OleDateStatusOff = 8;

    /// <summary>Offset of the u32 pad within an OLE date block. A set date repeats the double's high dword here.</summary>
    private const int OleDatePadOff = 12;

    /// <summary>OLE date status for "no date" (the block is <c>{0.0, 2, 0}</c>). A set date has status 0.</summary>
    private const uint OleDateStatusNull = 2;

 /// <summary>
 /// Returns true when the character is in the zone with a map. The roster and the guild panel's online count
 /// both use this check.
 /// </summary>
    public static bool IsOnline(PlayerStateManager pm, uint charId) => pm.GetPlayerByCharacterId(charId) is { MapId: not 0 };

    public static async Task<MemberView> ResolveMemberAsync(GuildMember m, PlayerStateManager pm, ICharacterRepository characters)
    {
        var p = pm.GetPlayerByCharacterId(m.CharId);
        
        
        
        var c = p?.Character ?? await characters.GetByIdAsync(m.CharId);
        if (c == null)
        {
            
            byte job = p?.Job ?? m.Job;
            return new MemberView(m, p?.Level ?? m.Level, job, job, 0, 0, 0, p?.MapId ?? 0);
        }
        var proJob = CharacterSerializer.WireProJob(c);
        return new MemberView(m, p?.Level ?? c.Level,
            WorldEntryService.CombatJobByte(c), WorldEntryService.CreationProfessionByte(c), c.JobLevel,
            proJob.Level, proJob.Id, p?.MapId ?? 0);
    }

    public static async Task<IReadOnlyList<MemberView>> ResolveMembersAsync(Guild g, PlayerStateManager pm, ICharacterRepository characters)
    {
        GuildMember[] members;
        lock (g.Gate) members = g.Members.ToArray();
        var views = new List<MemberView>(members.Length);
        foreach (var m in members) views.Add(await ResolveMemberAsync(m, pm, characters));
        return views;
    }

    
 /// <summary>
 /// Builds one member record of the roster. Fields: GID, level, combat class, creation profession, job level,
 /// guild bribe percent, contributed guild EXP, join date, rank index (0 is the master), ProJob level and id,
 /// current map id (0 when offline), and the name.
 /// </summary>
    public static byte[] BuildMemberRecord(in MemberView v)
    {
        var rec = new byte[MemberRecordLen];
        var s = rec.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[RecGidOff..], v.Member.Gid);
        s[RecLevelOff] = v.Level;
        s[RecClassOff] = v.Class;
        s[RecProfessionOff] = v.Profession;
        s[RecJobLevelOff] = v.JobLevel;
        s[RecBribePercentOff] = v.Member.BribePercent;   // guild bribe percent set by 1511
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(RecContribOff, sizeof(uint)),
            (uint)Math.Clamp(v.Member.ContribPoints, 0L, uint.MaxValue));   // contributed guild EXP
        WriteOleDate(s.Slice(RecJoinDateOff, OleDateLen), v.Member.JoinedUtc?.ToLocalTime());   // OLE dates are local time
        s[RecAuthIdOff] = v.Member.AuthId;
        s[RecProJobLevelOff] = v.ProJobLevel;
        s[RecProJobIdOff] = v.ProJobId;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(RecMapIdOff, sizeof(uint)), v.MapId);
        WriteWide(s.Slice(RecNameOff), v.Member.Name, RecNameMaxChars);
        return rec;
    }

    // 2560 AnsGuildMemList (U32 envelope): [u32 inner length][16 guild GUID][u16 member count][count x 115-byte record]
 /// <summary>
 /// Builds the roster, with members in ascending GID order.
 /// </summary>
    public static byte[] BuildAnsGuildMemList(Guild g, IReadOnlyList<MemberView> members)
    {
        using var w = new PacketWriter(32 + members.Count * MemberRecordLen);
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteBytes(GuidOf(g));
        w.WriteUInt16((ushort)members.Count);
        foreach (var v in members.OrderBy(v => v.Member.Gid)) w.WriteBytes(BuildMemberRecord(v));
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>
 /// Builds the empty roster of a guild with no members, keeping the 16-byte GUID the reader expects.
 /// </summary>
    public static byte[] BuildAnsGuildMemListEmpty(ReadOnlySpan<byte> guid16)
    {
        using var w = new PacketWriter(24);
        w.WriteUInt32(GuidLen + sizeof(ushort));   // inner length: the GUID and the zero count
        Span<byte> guid = stackalloc byte[GuidLen];
        guid.Clear();
        guid16[..Math.Min(GuidLen, guid16.Length)].CopyTo(guid);
        w.WriteBytes(guid);
        w.WriteUInt16(0);
        return w.ToArray();
    }

    /// <summary>First byte of the 2573 NfyGuildMemberAdd body.</summary>
    private const byte MemberAddLeadByte = 1;

 /// <summary>
 /// Builds the 2573 NfyGuildMemberAdd body: <c>[u8 1][16 guild GUID][115-byte member record]</c>.
 /// </summary>
    public static byte[] BuildNfyGuildMemberAdd(Guild g, in MemberView v)
    {
        using var w = new PacketWriter(140);
        w.WriteByte(MemberAddLeadByte);
        w.WriteBytes(GuidOf(g));
        w.WriteBytes(BuildMemberRecord(v));
        return w.ToArray();
    }

    // 2559 AnsMyGuildInfo, guild branch: two U32-length objects, a roster summary, then a fixed tail.
    // Object 1 (identity and robe): [u32 length][16 GUID][u8 name length][name][u16 emblem index][25-byte robe record]
    // Object 2 (notice and progress): [u32 length][16 GUID][u16 notice length][notice][39-byte progress block]
    // Progress block: [u32 nested empty blob][u64 EXP in level][16-byte OLE creation date][u8 level]
    //   [u8 unspent skill points][u8 0][u32 0][u32 0]

    // Length of the fixed part of object 1 (GUID, name length byte, emblem index, robe record); the name follows.
    private const int Info1Base = 44;

    // Length of the fixed part of object 2 (GUID, notice length, progress block); the notice follows.
    private const int Info2Base = 57;

    /// <summary>Character that ends a guild notice: U+F801, a private-use code point the client reads as the end of the text.</summary>
    private const string NoticeTerminator = "\uF801";

    /// <summary>Longest guild name written to the guild panel, in characters.</summary>
    private const int GuildNameMaxChars = 24;

    /// <summary>Longest guild master name written to the guild panel, in characters.</summary>
    private const int MasterNameMaxChars = 21;

    /// <summary>Length of the blob that closes the guild panel body; its content is not used and is sent zeroed.</summary>
    private const int PanelTrailerBlobLen = 152;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
 /// <summary>
 /// Returns the guild level and the experience within it for a guild with <paramref name="lifetimeExp"/>
 /// total experience. The level is capped at the table's last level. A new guild is level 1 with 0 experience.
 /// Level 0 is returned only when the table cannot be read.
 /// </summary>
    public static (byte Level, ulong Exp) GuildLevelOf(ulong lifetimeExp, IReadOnlyList<RO2.GameData.Tables.GuildExpTable>? table)
    {
        if (table is not { Count: > 0 }) return (0, lifetimeExp);
        var rows = table.OrderBy(r => r.Guild_Base_Level).ToArray();
        var rest = lifetimeExp;
        var i = 0;
        while (i < rows.Length - 1 && rows[i].Guild_Base_Exp_Next > 0 && rest >= rows[i].Guild_Base_Exp_Next)
        {
            rest -= rows[i].Guild_Base_Exp_Next;
            i++;
        }
        return (rows[i].Guild_Base_Level, rest);
    }

 /// <summary>
 /// Returns the guild's unspent skill points, the same value the guild skill learn handler spends, so a later
 /// guild panel never undoes a learn. Returns 0 before the game data is loaded.
 /// </summary>
    private static byte UnspentGuildSkillPoints(byte guildLevel, uint[] skills) =>
        RO2.GameData.GameDataStore.Loaded is { } data
            ? GuildSkillLearnPackets.UnspentPoints(guildLevel, skills, GuildSkillLearnPackets.Index(data.GuildSkillLevel))
            : (byte)0;

 /// <summary>
 /// Builds the guild branch of 2559: guild name and emblem, the current robe, the guild level and experience,
 /// the rank of the viewer, and the member counts and guild master. Members who are not online are counted as
 /// offline. Lists that are not identified are sent empty.
 /// </summary>
    public static byte[] BuildAnsMyGuildInfo(Guild g, uint viewerCharId, Func<uint, bool> isOnline, IGuildEmblemRepository emblems, IReadOnlyList<RO2.GameData.Tables.GuildExpTable> expTable)
    {
        GuildMember[] members;
        uint[] skills;
        lock (g.Gate) { members = g.Members.ToArray(); skills = g.Skills.ToArray(); }
        var master = members.FirstOrDefault(m => m.CharId == g.MasterCharId);
        var viewer = members.FirstOrDefault(m => m.CharId == viewerCharId);
        var online = members.Count(m => isOnline(m.CharId));

        var name = g.Name ?? string.Empty;
        if (name.Length > GuildNameMaxChars) name = name.Substring(0, GuildNameMaxChars);
        var notice = (g.Notice ?? string.Empty) + NoticeTerminator;
        var masterName = master?.Name ?? string.Empty;
        if (masterName.Length > MasterNameMaxChars) masterName = masterName.Substring(0, MasterNameMaxChars);

        using var w = new PacketWriter(320 + name.Length * 2 + notice.Length * 2);

        // Object 1: identity and current robe.
        w.WriteUInt32((uint)(Info1Base + name.Length * 2));
        w.WriteBytes(GuidOf(g));
        w.WriteByte((byte)name.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(name));
        w.WriteUInt16(emblems.IndexOf(GuidOf(g)));   
        w.WriteBytes(GuildRobeRecord.InfoForm());   // current robe: the Basic Robe

        // Object 2: notice and guild progress.
        w.WriteUInt32((uint)(Info2Base + notice.Length * 2));
        w.WriteBytes(GuidOf(g));
        w.WriteUInt16((ushort)notice.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(notice));
        var (guildLevel, levelExp) = GuildLevelOf(g.Exp, expTable);
        w.WriteUInt32(0);                          // nested U32-length blob, always empty
        w.WriteUInt64(levelExp);                   // guild EXP within the current level
        var date = new byte[OleDateLen];
        WriteOleDate(date, g.CreatedUtc?.ToLocalTime());   // creation date; null is the "no date" form
        w.WriteBytes(date);
        w.WriteByte(guildLevel);                   // guild level
        w.WriteByte(UnspentGuildSkillPoints(guildLevel, skills));   // unspent guild skill points
        w.WriteByte(0);                            // not used: sent as zero
        w.WriteUInt32(0);                          // not used: sent as zero
        w.WriteUInt32(0);                          // not used: sent as zero

        w.WriteByte(viewer?.AuthId ?? (byte)0);    // the viewer's own rank index

        // Object 3: roster summary, [u32 length][u16 member count][u16 online count][u8 master name length]
        // [master name][u16 0, empty list][u16 0, empty list].
        var p3Pos = w.Reserve(4);
        var p3Start = w.Length;
        w.WriteUInt16((ushort)members.Length);
        w.WriteUInt16((ushort)online);
        w.WriteByte((byte)masterName.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(masterName));
        w.WriteUInt16(0);                          // list not populated: empty
        w.WriteUInt16(0);                          // list not populated: empty
        w.WriteAt(p3Pos, (uint)(w.Length - p3Start));

        w.WriteZeros(PanelTrailerBlobLen);
        w.WriteCompactLength(0);                   // trailing u32 list, empty
        return w.ToArray();
    }

    // 2628 AnsGuildAuthority (U32 envelope): [u32 inner length][u16 rank count][count x 24-byte rank record]

    /// <summary>Length of a rank record.</summary>
    internal const int RankRecordLen = 24;

    /// <summary>Longest rank title in characters: the 11-character name field minus its terminator.</summary>
    internal const int RankNameMaxChars = 10;

    // Rank record layout: [u8 index][22-byte UTF-16LE name, NUL-terminated][u8 permissions]
    private const int RankIndexOff = 0;
    private const int RankNameOff = 1;
    private const int RankNameBytes = 22;
    private const int RankPermissionsOff = 23;

 /// <summary>
 /// Builds one rank title: index, name (NUL-terminated, 11 characters) and permissions. The same 24 bytes are
 /// used by the authority update and the rank request.
 /// </summary>
    public static byte[] BuildRankRecord(GuildRank r)
    {
        var rec = new byte[RankRecordLen];
        rec[RankIndexOff] = r.Index;
        WriteWide(rec.AsSpan(RankNameOff, RankNameBytes), r.Name, RankNameMaxChars);
        rec[RankPermissionsOff] = r.Permissions;
        return rec;
    }

 /// <summary>
 /// Builds the guild's rank table in stored order. A null guild produces the empty table.
 /// </summary>
    public static byte[] BuildAnsGuildAuthority(Guild? g)
    {
        GuildRank[] ranks;
        if (g == null) ranks = Array.Empty<GuildRank>();
        else lock (g.Gate) ranks = g.Ranks.ToArray();
        using var w = new PacketWriter(8 + ranks.Length * RankRecordLen);
        var lenPos = w.Reserve(4);
        var start = w.Length;
        w.WriteUInt16((ushort)ranks.Length);
        foreach (var r in ranks) w.WriteBytes(BuildRankRecord(r));
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    private static byte[] GuidOf(Guild g)
    {
        var b = new byte[GuidLen];
        if (g.Guid16 != null) Array.Copy(g.Guid16, b, Math.Min(GuidLen, g.Guid16.Length));
        return b;
    }

 /// <summary>
 /// Writes an OLE date as {f64 date, u32 status, u32 pad}. A set date has status 0 and repeats the high dword of
 /// the double in the pad. A null date is written as {0, 2, 0}.
 /// </summary>
    private static void WriteOleDate(Span<byte> dst, DateTime? when)
    {
        dst[..OleDateLen].Clear();
        if (when is null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(OleDateStatusOff, sizeof(uint)), OleDateStatusNull);
            return;
        }
        var d = when.Value.ToOADate();
        BinaryPrimitives.WriteDoubleLittleEndian(dst, d);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(OleDatePadOff, sizeof(uint)), (uint)(BitConverter.DoubleToInt64Bits(d) >> 32));
    }

    private static void WriteWide(Span<byte> dst, string? s, int maxChars)
    {
        s ??= string.Empty;
        var n = Math.Min(s.Length, maxChars);
        for (int i = 0; i < n && i * 2 + 1 < dst.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(i * 2, 2), s[i]);
    }
}
