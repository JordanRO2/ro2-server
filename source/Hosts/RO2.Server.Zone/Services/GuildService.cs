using System.Collections.Concurrent;
using MongoDB.Bson.Serialization.Attributes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Repositories;

namespace RO2.Server.Zone.Services;

/// <summary>One persisted guild member, keyed by the character's 64-bit GID.</summary>
public sealed class GuildMember
{
    public uint CharId { get; set; }
    public ulong Gid { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Level { get; set; } = GuildService.MinMemberLevel;
    public byte Job { get; set; }
 /// <summary>Rank: the index of a title in the guild's rank table. Index 0 is the master's title.</summary>
    public byte AuthId { get; set; }
 /// <summary>
 /// Guild EXP this member has contributed over time. Credited with each donation and handed to the master when the
 /// member leaves.
 /// </summary>
    public long ContribPoints { get; set; }
 /// <summary>
 /// The member's EXP donation rate in percent, set by the member and kept across sessions. It is the share of each
 /// quest prize's EXP that is donated to the guild. The default 0 donates nothing.
 /// </summary>
    public byte BribePercent { get; set; }
 /// <summary>
 /// When the member joined. Null for members stored before this was recorded.
 /// </summary>
    public DateTime? JoinedUtc { get; set; }
}

/// <summary>One title of a guild's rank table.</summary>
public sealed class GuildRank
{
 /// <summary>Rank index, from 0 (master) to 9.</summary>
    public byte Index { get; set; }
 /// <summary>The title as sent to the client.</summary>
    public string Name { get; set; } = string.Empty;
 /// <summary>Permission bits of the title.</summary>
    public byte Permissions { get; set; }
}

/// <summary>One item parked in the guild vault. The full item instance is kept, so a withdraw restores it exactly.</summary>
public sealed class GuildStorageItem
{
    /// <summary>Durability value of an item that has none (not wearable gear).</summary>
    public const short NoDurability = -1;

    public ulong Uid { get; set; }
    public uint ItemId { get; set; }
    public int Count { get; set; } = 1;
    public byte Refine { get; set; }
    public short Durability { get; set; } = NoDurability;
    public string? CardsJson { get; set; }
 /// <summary>
 /// Remaining instance state of the item: bestowed element, dye colour and rune-hole punchers. Vault items stored
 /// before these fields existed load with neutral values.
 /// </summary>
    public byte Element { get; set; }
    public uint DyeColor { get; set; }
    public string? SocketPunchersJson { get; set; }
 /// <summary>
 /// Remaining per-instance state of the item (second and third dye colours, enchant data and bind history),
 /// carried for the same reason. Rows stored before these existed load with zero or false.
 /// </summary>
    public uint DyeColor2 { get; set; }
    public uint DyeColor3 { get; set; }
    public uint FxEnchantIndex { get; set; }
    public ushort EnchantReinforceId { get; set; }
    public ushort EnchantSeconds { get; set; }
    public bool Bound { get; set; }
 /// <summary>
 /// The item's own ability value, or null to use the template's value. Rows stored before this existed load with
 /// null.
 /// </summary>
    public uint? AbilityValue { get; set; }
 /// <summary>Vault slot index, from 0 to N.</summary>
    public byte StorageSlot { get; set; }
    public uint DepositedByCharId { get; set; }
}

/// <summary>
/// A guild aggregate. It is persisted as one document and is the live in-memory object that every handler
/// modifies.
/// </summary>
public sealed class Guild
{
    /// <summary>Length in bytes of <see cref="Guid16"/>.</summary>
    public const int GuidLength = 16;

    [BsonId] public uint Id { get; set; }
 /// <summary>The 16-byte key the client uses to address the guild.</summary>
    public byte[] Guid16 { get; set; } = new byte[GuidLength];
    public string Name { get; set; } = string.Empty;
    public uint MasterCharId { get; set; }
    public string Notice { get; set; } = string.Empty;
    public long Gold { get; set; }
 /// <summary>When the guild was created. Null for guilds created before this was recorded.</summary>
    public DateTime? CreatedUtc { get; set; }
 /// <summary>
 /// Lifetime guild EXP, summed over every credit. The guild level and the EXP within that level are derived from it
 /// on every use, so the two cannot drift apart. Guilds stored before this field existed load with 0.
 /// </summary>
    public ulong Exp { get; set; }
 /// <summary>The guild's rank table, seeded with the default titles on creation and on load.</summary>
    public List<GuildRank> Ranks { get; set; } = new();
    public List<GuildMember> Members { get; set; } = new();
    public List<GuildStorageItem> Storage { get; set; } = new();
    public List<uint> Skills { get; set; } = new();

 /// <summary>
 /// Lock that serialises every read and write of this guild's lists and fields. It is process state and is not
 /// stored.
 /// </summary>
    [BsonIgnore] [System.Text.Json.Serialization.JsonIgnore] public Lock Gate { get; } = new();
}

/// <summary>A pending guild invite, kept until the invitee answers.</summary>
public sealed class GuildPendingInvite
{
    public uint GuildId { get; set; }
    public uint InviterCharId { get; set; }
 /// <summary>
 /// The 16-byte token of the invite popup. The invitee's answer must echo the token of the latest invite.
 /// </summary>
    public byte[] Token { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Process-wide in-memory guild registry. Every mutation is written straight through to the guild store. Item
/// movement goes through the inventory repository that the caller passes in.
/// </summary>
public sealed class GuildService
{
    public static readonly GuildService Instance = new();

    private readonly ConcurrentDictionary<uint, Guild> _guilds = new();      
    private readonly ConcurrentDictionary<uint, uint> _byChar = new();       
    private readonly ConcurrentDictionary<uint, GuildPendingInvite> _invites = new(); 

    private IGuildStore? _repo;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _loaded;
    private int _nextId = 1;

    private GuildService() { }

 /// <summary>
 /// Returns the member GID of a character: the same value the character uses to log in and that the character's own
 /// data carries, so the client finds itself in the guild roster.
 /// </summary>
    public static ulong GidFor(uint charId) => CharacterSerializer.GenerateUid(charId);

 /// <summary>
 /// Sets every member's GID to the value <see cref="GidFor"/> assigns, repairing members stored under an older
 /// value. Returns true when anything changed, so the caller persists it.
 /// </summary>
    internal static bool NormalizeGids(Guild g)
    {
        lock (g.Gate)
        {
            var changed = false;
            foreach (var m in g.Members)
            {
                var want = GidFor(m.CharId);
                if (m.Gid == want) continue;
                m.Gid = want;
                changed = true;
            }
            return changed;
        }
    }

    
 /// <summary>
 /// Rank index of the master. Only the master holds it, and it changes hands only by handover.
 /// </summary>
    public const byte MasterRankIndex = 0;

 /// <summary>Level a member is stored with when the character reports none (level 0).</summary>
    public const byte MinMemberLevel = 1;

 /// <summary>Rank index of the member title in a default two-title table.</summary>
    private const byte DefaultMemberRankIndex = 1;

 /// <summary>
 /// Title text of the default master rank: a client string id (string_ui 11832, "master") behind a leading space,
 /// sent verbatim in place of a name.
 /// </summary>
    private const string DefaultMasterTitle = " 11832";

 /// <summary>Title text of the default member rank: a client string id (string_ui 11833, "member"), as above.</summary>
    private const string DefaultMemberTitle = " 11833";

 /// <summary>Permission byte with every one of the four rights (invite, remove member, storage, notice) set.</summary>
    private const byte PermissionsAll = 0x0F;

 /// <summary>Permission byte with no right set.</summary>
    private const byte PermissionsNone = 0x00;

 /// <summary>
 /// The rank table of a newly created guild: a master title with all permissions and a member title with none.
 /// </summary>
    private static List<GuildRank> DefaultRanks() => new()
    {
        new GuildRank { Index = MasterRankIndex, Name = DefaultMasterTitle, Permissions = PermissionsAll },
        new GuildRank { Index = DefaultMemberRankIndex, Name = DefaultMemberTitle, Permissions = PermissionsNone },
    };

 /// <summary>
 /// Returns the rank a joining member gets, and the rank a replaced master drops to: the highest index in the table.
 /// </summary>
    public static byte LowestRankIndex(Guild g)
    {
        lock (g.Gate) return g.Ranks.Count == 0 ? MasterRankIndex : g.Ranks.Max(r => r.Index);
    }

 /// <summary>
 /// Permission bit to remove members. Bit 0 is the invite permission, and the bits follow the order of the client's
 /// permission columns: invite, remove member, storage, notice.
 /// </summary>
    public const byte PermissionKick = 0x02;

 /// <summary>Permission bit to invite members.</summary>
    public const byte PermissionInvite = 0x01;

 /// <summary>
 /// Returns whether <paramref name="charId"/> holds the permission bits <paramref name="permission"/> in
 /// <paramref name="g"/>. The master always holds them. A non-member holds none.
 /// </summary>
    public static bool HasPermission(Guild g, uint charId, byte permission)
    {
        lock (g.Gate)
        {
            if (g.MasterCharId == charId) return true;
            var m = g.Members.FirstOrDefault(x => x.CharId == charId);
            if (m == null) return false;
            var rank = g.Ranks.FirstOrDefault(r => r.Index == m.AuthId);
            return rank != null && (rank.Permissions & permission) == permission;
        }
    }

 /// <summary>
 /// Returns the name of the title <paramref name="charId"/> holds in <paramref name="g"/>, or an empty string for a
 /// non-member or a member whose index is missing from the table.
 /// </summary>
    public static string RankTitleOf(Guild g, uint charId)
    {
        lock (g.Gate)
        {
            var m = g.Members.FirstOrDefault(x => x.CharId == charId);
            if (m == null) return string.Empty;
            return g.Ranks.FirstOrDefault(r => r.Index == m.AuthId)?.Name ?? string.Empty;
        }
    }

 /// <summary>
 /// Seeds the default rank table where a guild has none, and re-derives every member's rank from it. Members on a
 /// missing index are moved to the lowest rank. Returns true when anything changed, so the caller persists it.
 /// </summary>
    private static bool NormalizeRanks(Guild g)
    {
        lock (g.Gate)
        {
            var changed = false;
            if (g.Ranks == null) g.Ranks = new List<GuildRank>();
            var defaults = DefaultRanks();
            
            
            if (g.Ranks.All(r => r.Index != MasterRankIndex))
            {
                g.Ranks.Insert(0, defaults[MasterRankIndex]);
                changed = true;
            }
            if (g.Ranks.Count < MinRanks)
            {
                g.Ranks.Add(defaults[DefaultMemberRankIndex]);
                changed = true;
            }
            var lowest = g.Ranks.Max(r => r.Index);
            foreach (var m in g.Members)
            {
                var want = m.CharId == g.MasterCharId ? MasterRankIndex
                    : m.AuthId == MasterRankIndex || g.Ranks.All(r => r.Index != m.AuthId) ? lowest
                    : m.AuthId;
                if (m.AuthId == want) continue;
                m.AuthId = want;
                changed = true;
            }
            return changed;
        }
    }

 /// <summary>
 /// Loads every guild from the store once, and sets the store used for all later writes. Safe to call from every
 /// handler.
 /// </summary>
    public async Task InitAsync(IGuildStore repo)
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            _repo = repo;
            var repaired = new List<Guild>();
            foreach (var g in await repo.LoadAllAsync())
            {
                if (g.Guid16 == null || g.Guid16.Length != Guild.GuidLength) g.Guid16 = new byte[Guild.GuidLength];
                
                if (NormalizeRanks(g) | NormalizeGids(g)) repaired.Add(g);
                _guilds[g.Id] = g;
                foreach (var m in g.Members) _byChar[m.CharId] = g.Id;
                if (g.Id >= _nextId) _nextId = (int)g.Id + 1;
            }
            foreach (var g in repaired) await PersistAsync(g);
            _loaded = true;
        }
        finally { _initLock.Release(); }
    }

    public Guild? GetById(uint guildId) => _guilds.TryGetValue(guildId, out var g) ? g : null;

    public Guild? GetByCharId(uint charId)
        => _byChar.TryGetValue(charId, out var id) && _guilds.TryGetValue(id, out var g) ? g : null;

    public Guild? GetByGuid(ReadOnlySpan<byte> guid16)
    {
        foreach (var g in _guilds.Values)
            if (g.Guid16.SequenceEqual(guid16)) return g;
        return null;
    }

 /// <summary>
 /// Returns whether a registered guild already has <paramref name="name"/>, compared case-insensitively. The
 /// caller's own guild counts as taken too.
 /// </summary>
    public bool IsNameTaken(string name)
    {
        lock (_createGate)
        {
            foreach (var held in _heldNames)
                if (string.Equals(held, name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var g in _guilds.Values)
                if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    
 /// <summary>Why <see cref="CreateAsync"/> did not create a guild.</summary>
    public enum CreateRefusal
    {
        None,
 /// <summary>The founder is already in a guild.</summary>
        AlreadyInGuild,
 /// <summary>A registered guild already has the name.</summary>
        NameTaken,
    }

 /// <summary>
 /// Serialises the check and the write of guild names, so two concurrent creations or renames cannot both take the
 /// same name. Lock order: this gate first, then a guild's own gate.
 /// </summary>
    private readonly Lock _createGate = new();

 /// <summary>
 /// Old names of guilds whose rename is waiting for its payment. Each stays taken until the payment settles, so a
 /// failed payment can return the old name. Read and written only under the name gate.
 /// </summary>
    private readonly List<string> _heldNames = new();

 /// <summary>
 /// Registers a new guild with <paramref name="masterCharId"/> as its only member and master. Refused when the founder
 /// already has a guild or the name is taken.
 /// </summary>
    public async Task<(Guild? Guild, CreateRefusal Refusal)> CreateAsync(uint masterCharId, string masterName, byte masterLevel, byte masterJob, string name)
    {
        Guild g;
        lock (_createGate)
        {
            if (GetByCharId(masterCharId) != null) return (null, CreateRefusal.AlreadyInGuild);
            if (IsNameTaken(name)) return (null, CreateRefusal.NameTaken);
            g = new Guild
            {
                Id = (uint)Interlocked.Increment(ref _nextId) - 1,
                Guid16 = Guid.NewGuid().ToByteArray(),
                Name = name,
                MasterCharId = masterCharId,
                Notice = string.Empty,
                
                
                CreatedUtc = DateTime.UtcNow,
                Ranks = DefaultRanks(),
            };
            g.Members.Add(new GuildMember
            {
                CharId = masterCharId, Gid = GidFor(masterCharId), Name = masterName,
                Level = masterLevel == 0 ? MinMemberLevel : masterLevel, Job = masterJob, AuthId = MasterRankIndex,
                
                
                JoinedUtc = g.CreatedUtc,
            });
            _guilds[g.Id] = g;
            _byChar[masterCharId] = g.Id;
        }
        await PersistAsync(g);
        return (g, CreateRefusal.None);
    }

 /// <summary>Why <see cref="RenameAsync"/> did not change the guild's name.</summary>
    public enum RenameRefusal
    {
        None,
 /// <summary>The guild was disbanded before the call.</summary>
        NotRegistered,
 /// <summary>A registered guild, the renamed one included, already has the name.</summary>
        NameTaken,
    }

 /// <summary>
 /// Gives <paramref name="g"/> the name <paramref name="name"/> when no registered guild has it, paying with
 /// <paramref name="pay"/>.
 /// </summary>
 /// <remarks>
 /// The name is taken before the payment. While the payment runs, the old name stays reserved, so it can be restored
 /// if the payment fails. The exception from a failed payment is rethrown to the caller.
 /// </remarks>
 /// <returns>The refusal, <see cref="RenameRefusal.None"/> on success, and the name the guild had before.</returns>
    public async Task<(RenameRefusal Refusal, string OldName)> RenameAsync(Guild g, string name, Func<Task> pay)
    {
        string oldName;
        lock (_createGate)
        {
            lock (g.Gate) oldName = g.Name;
            if (!IsRegistered(g)) return (RenameRefusal.NotRegistered, oldName);
            if (IsNameTaken(name)) return (RenameRefusal.NameTaken, oldName);
            lock (g.Gate) g.Name = name;
            _heldNames.Add(oldName);
        }

        try
        {
            await pay();
        }
        catch
        {
            lock (_createGate)
            {
                lock (g.Gate) if (g.Name == name) g.Name = oldName;
                _heldNames.Remove(oldName);
            }
            
            
            await PersistAsync(g);
            throw;
        }

        lock (_createGate) _heldNames.Remove(oldName);
        await PersistAsync(g);
        return (RenameRefusal.None, oldName);
    }

    public async Task DisbandAsync(Guild g)
    {
        _guilds.TryRemove(g.Id, out _);
        foreach (var m in g.Members) _byChar.TryRemove(m.CharId, out _);
        try { if (_repo != null) await _repo.DeleteAsync(g.Id); } catch {   }
    }

 /// <summary>
 /// Disbands <paramref name="g"/> when <paramref name="refusal"/> returns 0. The check and the removal happen under the
 /// guild's gate, so the conditions still hold at the moment of disbanding. Returns the refusal code, or 0 when the
 /// guild was disbanded.
 /// </summary>
    public async Task<ushort> TryDisbandAsync(Guild g, Func<Guild, ushort> refusal)
    {
        lock (g.Gate)
        {
            var r = refusal(g);
            if (r != 0) return r;
            _guilds.TryRemove(g.Id, out _);
            foreach (var m in g.Members) _byChar.TryRemove(m.CharId, out _);
        }
        try { if (_repo != null) await _repo.DeleteAsync(g.Id); } catch {   }
        return 0;
    }

 /// <summary>
 /// Returns true while <paramref name="g"/> is the registered guild for its id. False once it has been disbanded.
 /// </summary>
    private bool IsRegistered(Guild g) => _guilds.TryGetValue(g.Id, out var live) && ReferenceEquals(live, g);

    public async Task<GuildMember> AddMemberAsync(Guild g, uint charId, string name, byte level, byte job)
    {
        GuildMember m;
        lock (g.Gate)
        {
            m = g.Members.FirstOrDefault(x => x.CharId == charId)!;
            if (m == null)
            {
                
                
                m = new GuildMember
                {
                    CharId = charId, Gid = GidFor(charId), Name = name,
                    Level = level == 0 ? MinMemberLevel : level, Job = job, AuthId = LowestRankIndex(g),
                    JoinedUtc = DateTime.UtcNow,   
                };
                g.Members.Add(m);
            }
        }
        _byChar[charId] = g.Id;
        await PersistAsync(g);
        return m;
    }

    public async Task<GuildMember?> RemoveMemberByCharIdAsync(Guild g, uint charId)
    {
        GuildMember? removed;
        lock (g.Gate)
        {
            removed = g.Members.FirstOrDefault(x => x.CharId == charId);
            if (removed != null) g.Members.Remove(removed);
        }
        if (removed != null)
        {
            _byChar.TryRemove(charId, out _);
            await PersistAsync(g);
        }
        return removed;
    }

    public GuildMember? FindMemberByGid(Guild g, ulong gid) => g.Members.FirstOrDefault(m => m.Gid == gid);

    
    public async Task SetNoticeAsync(Guild g, string notice)
    {
        g.Notice = notice ?? string.Empty;
        await PersistAsync(g);
    }

 /// <summary>
 /// Gives a member one of the guild's titles. Refused for a title the table does not hold, for the master's title,
 /// and for the master.
 /// </summary>
    public async Task<bool> SetAuthAsync(Guild g, ulong gid, byte authId)
    {
        var m = FindMemberByGid(g, gid);
        if (m == null) return false;
        lock (g.Gate)
        {
            if (m.CharId == g.MasterCharId || authId == MasterRankIndex || g.Ranks.All(r => r.Index != authId))
                return false;
            m.AuthId = authId;
        }
        await PersistAsync(g);
        return true;
    }

 /// <summary>
 /// Hands the master title to another member. The new master takes rank 0, and the old master drops to the lowest
 /// rank, if still a member.
 /// </summary>
    public async Task<bool> SetMasterAsync(Guild g, ulong newMasterGid)
    {
        var m = FindMemberByGid(g, newMasterGid);
        if (m == null) return false;
        lock (g.Gate)
        {
            var old = g.Members.FirstOrDefault(x => x.CharId == g.MasterCharId);
            if (old != null && !ReferenceEquals(old, m)) old.AuthId = LowestRankIndex(g);
            g.MasterCharId = m.CharId;
            m.AuthId = MasterRankIndex;
        }
        await PersistAsync(g);
        return true;
    }

 /// <summary>Maximum number of titles a rank table may hold, indices 0 to 9.</summary>
    public const int MaxRanks = 10;

 /// <summary>Why <see cref="ModifyRankAsync"/> did not change the rank table.</summary>
    public enum ModifyRankRefusal
    {
        None,
 /// <summary>The guild was disbanded before the call.</summary>
        NotRegistered,
 /// <summary>The requester is not the guild master, the only member allowed to edit titles.</summary>
        NotMaster,
 /// <summary>The index is outside the table.</summary>
        OutOfRange,
 /// <summary>Another index of the table already has this title.</summary>
        DuplicateName,
    }

 /// <summary>
 /// Stores one title record for the table: the index, the name and the permission bits. Replaces the title at that
 /// index or adds it. The checks and the write happen in one step under the guild's gate.
 /// </summary>
 /// <returns>The refusal and, on success, a copy of the stored title.</returns>
    public async Task<(ModifyRankRefusal Refusal, GuildRank? Rank)> ModifyRankAsync(Guild g, uint charId, byte index, string name, byte permissions)
    {
        ModifyRankRefusal refusal;
        GuildRank? stored;
        lock (g.Gate)
        {
            if (!IsRegistered(g)) return (ModifyRankRefusal.NotRegistered, null);
            refusal = TryModifyRank(g, charId, index, name, permissions, out stored);
        }
        if (refusal == ModifyRankRefusal.None) await PersistAsync(g);
        return (refusal, stored);
    }

 /// <summary>
 /// Applies the rules of a title edit to <paramref name="g"/> in memory, without writing. A replaced title keeps its
 /// position and a new one is appended. Only the master may edit titles, and a title may not repeat the name of
 /// another index.
 /// </summary>
 /// <remarks>
 /// Reentrant on the guild's gate, so <see cref="ModifyRankAsync"/> can call it while holding the gate.
 /// </remarks>
    internal static ModifyRankRefusal TryModifyRank(Guild g, uint charId, byte index, string? name, byte permissions, out GuildRank? stored)
    {
        stored = null;
        var title = name ?? string.Empty;
        lock (g.Gate)
        {
            if (g.MasterCharId != charId) return ModifyRankRefusal.NotMaster;
            if (index >= MaxRanks) return ModifyRankRefusal.OutOfRange;
            if (g.Ranks.Any(r => r.Index != index && string.Equals(r.Name, title, StringComparison.Ordinal)))
                return ModifyRankRefusal.DuplicateName;
            var rank = g.Ranks.FirstOrDefault(r => r.Index == index);
            if (rank == null)
            {
                rank = new GuildRank { Index = index };
                g.Ranks.Add(rank);
            }
            rank.Name = title;
            rank.Permissions = permissions;
            stored = new GuildRank { Index = rank.Index, Name = rank.Name, Permissions = rank.Permissions };
            return ModifyRankRefusal.None;
        }
    }

 /// <summary>
 /// Returns the members who hold the title at <paramref name="index"/>, by character id.
 /// </summary>
    public static uint[] HoldersOf(Guild g, byte index)
    {
        lock (g.Gate) return g.Members.Where(m => m.AuthId == index).Select(m => m.CharId).ToArray();
    }

 /// <summary>Why <see cref="DeleteRankAsync"/> did not remove the title.</summary>
    public enum DeleteRankRefusal
    {
        None,
 /// <summary>The guild was disbanded before the call.</summary>
        NotRegistered,
 /// <summary>The requester is not the guild master, the only member allowed to edit titles.</summary>
        NotMaster,
 /// <summary>The master's title cannot be deleted.</summary>
        MasterTitle,
 /// <summary>The index is outside the table.</summary>
        OutOfRange,
 /// <summary>No title with that index is in the table.</summary>
        NoSuchTitle,
 /// <summary>A member still holds the title.</summary>
        TitleInUse,
 /// <summary>Deleting the title would leave fewer than the minimum number of titles.</summary>
        TooFewTitles,
    }

 /// <summary>Minimum number of titles a rank table keeps.</summary>
    public const int MinRanks = 2;

 /// <summary>
 /// Deletes the title at <paramref name="index"/> from the guild's rank table, on behalf of <paramref name="charId"/>.
 /// The checks and the removal happen in one step under the guild's gate.
 /// </summary>
    public async Task<DeleteRankRefusal> DeleteRankAsync(Guild g, uint charId, byte index)
    {
        DeleteRankRefusal refusal;
        lock (g.Gate)
        {
            if (!IsRegistered(g)) return DeleteRankRefusal.NotRegistered;
            refusal = TryDeleteRank(g, charId, index);
        }
        if (refusal == DeleteRankRefusal.None) await PersistAsync(g);
        return refusal;
    }

 /// <summary>
 /// Applies the rules of a title deletion to <paramref name="g"/> in memory, without writing. The removal happens only
 /// when every rule passes. Only the master may delete titles, the master's title is never deleted, and a title held
 /// by a member cannot be deleted.
 /// </summary>
    internal static DeleteRankRefusal TryDeleteRank(Guild g, uint charId, byte index)
    {
        lock (g.Gate)
        {
            if (g.MasterCharId != charId) return DeleteRankRefusal.NotMaster;
            if (index == MasterRankIndex) return DeleteRankRefusal.MasterTitle;
            if (index >= MaxRanks) return DeleteRankRefusal.OutOfRange;
            var rank = g.Ranks.FirstOrDefault(r => r.Index == index);
            if (rank == null) return DeleteRankRefusal.NoSuchTitle;
            if (g.Members.Any(m => m.AuthId == index)) return DeleteRankRefusal.TitleInUse;
            if (g.Ranks.Count - 1 < MinRanks) return DeleteRankRefusal.TooFewTitles;
            g.Ranks.Remove(rank);
            return DeleteRankRefusal.None;
        }
    }

    
 /// <summary>Why a funds move did not change the guild's funds.</summary>
    public enum FundsRefusal
    {
        None,
 /// <summary>The guild was disbanded before the call.</summary>
        NotRegistered,
 /// <summary>The deposit would exceed the guild's storage limit.</summary>
        OverLimit,
 /// <summary>The withdrawal asks for more than the guild holds.</summary>
        NotEnoughFunds,
 /// <summary>The guild document could not be written. The move was undone in memory.</summary>
        WriteFailed,
    }

 /// <summary>
 /// Adds <paramref name="amount"/> zeny, already taken from the depositor's wallet, to the guild's funds, unless the
 /// result would exceed <paramref name="limit"/>. The funds are written before this returns. On a write failure the
 /// add is undone, and the caller must return the zeny to the wallet.
 /// </summary>
    public async Task<(FundsRefusal Refusal, long FundsAfter)> AddFundsAsync(Guild g, long amount, long limit)
    {
        long after;
        lock (g.Gate)
        {
            if (!IsRegistered(g)) return (FundsRefusal.NotRegistered, 0);
            if (!TryAddFunds(g, amount, limit, out after)) return (FundsRefusal.OverLimit, after);
        }
        if (await TryPersistAsync(g)) return (FundsRefusal.None, after);
        long back;
        lock (g.Gate) { g.Gold -= amount; back = g.Gold; }
        await PersistAsync(g);   
        return (FundsRefusal.WriteFailed, back);
    }

 /// <summary>
 /// Takes <paramref name="amount"/> zeny from the guild's funds, unless the guild holds less. The funds are written
 /// before the caller credits any wallet. On a write failure the take is undone. If the wallet credit fails later,
 /// <see cref="ReturnFundsAsync"/> returns the zeny.
 /// </summary>
    public async Task<(FundsRefusal Refusal, long FundsAfter)> TakeFundsAsync(Guild g, long amount)
    {
        long after;
        lock (g.Gate)
        {
            if (!IsRegistered(g)) return (FundsRefusal.NotRegistered, 0);
            if (!TryTakeFunds(g, amount, out after)) return (FundsRefusal.NotEnoughFunds, after);
        }
        if (await TryPersistAsync(g)) return (FundsRefusal.None, after);
        long back;
        lock (g.Gate) { g.Gold += amount; back = g.Gold; }
        await PersistAsync(g);
        return (FundsRefusal.WriteFailed, back);
    }

 /// <summary>
 /// Returns zeny to the guild's funds after a <see cref="TakeFundsAsync"/> whose wallet credit failed. There is no
 /// limit check. Returns the funds after.
 /// </summary>
    public async Task<long> ReturnFundsAsync(Guild g, long amount)
    {
        long after;
        lock (g.Gate)
        {
            g.Gold = g.Gold > long.MaxValue - amount ? long.MaxValue : g.Gold + amount;
            after = g.Gold;
        }
        await PersistAsync(g);
        return after;
    }

 /// <summary>
 /// Applies a deposit to the funds in memory. The deposit is accepted when the result stays at or under
 /// <paramref name="limit"/>.
 /// </summary>
    internal static bool TryAddFunds(Guild g, long amount, long limit, out long after)
    {
        lock (g.Gate)
        {
            if (amount < 0 || g.Gold > limit || amount > limit - g.Gold)
            {
                after = g.Gold;
                return false;
            }
            g.Gold += amount;
            after = g.Gold;
            return true;
        }
    }

 /// <summary>
 /// Applies a withdrawal to the funds in memory. The withdrawal is accepted when the guild holds at least
 /// <paramref name="amount"/>.
 /// </summary>
    internal static bool TryTakeFunds(Guild g, long amount, out long after)
    {
        lock (g.Gate)
        {
            if (amount < 0 || amount > g.Gold)
            {
                after = g.Gold;
                return false;
            }
            g.Gold -= amount;
            after = g.Gold;
            return true;
        }
    }

 /// <summary>
 /// Credits guild EXP and persists it. Returns the lifetime EXP before and after the credit, so the caller can tell
 /// whether the guild levelled up.
 /// </summary>
 /// <remarks>
 /// This credits the guild only. A member's contribution is credited by <see cref="CreditMemberExpAsync"/>.
 /// </remarks>
    public async Task<(ulong Before, ulong After)> AddExpAsync(Guild g, ulong amount)
    {
        ulong before, after;
        lock (g.Gate)
        {
            
            const ulong Cap = long.MaxValue;
            before = g.Exp;
            after = before >= Cap || amount > Cap - before ? Cap : before + amount;
            g.Exp = after;
        }
        if (after != before) await PersistAsync(g);
        return (before, after);
    }

 /// <summary>
 /// Credits guild EXP donated by member <paramref name="charId"/>, to the guild and to the member's own contribution,
 /// in one write. Returns the lifetime EXP before and after, as <see cref="AddExpAsync"/> does.
 /// </summary>
 /// <remarks>
 /// A member who has left meanwhile credits the guild only.
 /// </remarks>
    public async Task<(ulong Before, ulong After)> CreditMemberExpAsync(Guild g, uint charId, ulong amount)
    {
        ulong before, after;
        lock (g.Gate)
        {
            const ulong Cap = long.MaxValue;   
            before = g.Exp;
            after = before >= Cap || amount > Cap - before ? Cap : before + amount;
            g.Exp = after;
            if (g.Members.FirstOrDefault(x => x.CharId == charId) is { } m)
            {
                var credit = (long)Math.Min(amount, (ulong)long.MaxValue);
                m.ContribPoints = m.ContribPoints > long.MaxValue - credit ? long.MaxValue : m.ContribPoints + credit;
            }
        }
        if (amount > 0) await PersistAsync(g);
        return (before, after);
    }

 /// <summary>The highest donation rate a member may set, in percent.</summary>
    public const byte MaxBribePercent = 50;

 /// <summary>
 /// Stores a member's donation rate, capped at <see cref="MaxBribePercent"/>. Returns the stored rate, or null when the
 /// character is not a member.
 /// </summary>
    public async Task<byte?> SetBribePercentAsync(Guild g, uint charId, byte percent)
    {
        byte stored;
        lock (g.Gate)
        {
            if (g.Members.FirstOrDefault(x => x.CharId == charId) is not { } m) return null;
            stored = Math.Min(percent, MaxBribePercent);
            if (m.BribePercent == stored) return stored;
            m.BribePercent = stored;
        }
        await PersistAsync(g);
        return stored;
    }

 /// <summary>Returns a member's donation rate, or 0 for a non-member.</summary>
    public static byte BribePercentOf(Guild g, uint charId)
    {
        lock (g.Gate) return g.Members.FirstOrDefault(x => x.CharId == charId)?.BribePercent ?? 0;
    }

 /// <summary>
 /// Hands a leaving member's contribution to the guild master. Call it after the member is removed, with the removed
 /// record. Returns the master's contribution total after the handover, or null when the master is not a member.
 /// </summary>
    public async Task<long?> HandOverContributionAsync(Guild g, GuildMember left)
    {
        long total;
        lock (g.Gate)
        {
            if (g.Members.FirstOrDefault(x => x.CharId == g.MasterCharId) is not { } master) return null;
            var moved = Math.Max(0, left.ContribPoints);
            master.ContribPoints = master.ContribPoints > long.MaxValue - moved ? long.MaxValue : master.ContribPoints + moved;
            total = master.ContribPoints;
        }
        if (left.ContribPoints > 0) await PersistAsync(g);
        return total;
    }

    public async Task AddSkillAsync(Guild g, uint skillId)
    {
        lock (g.Gate) { if (!g.Skills.Contains(skillId)) g.Skills.Add(skillId); }
        await PersistAsync(g);
    }

    
 /// <summary>Serialises the check and the add of invites.</summary>
    private readonly Lock _inviteGate = new();

 /// <summary>
 /// Records an invite that the invitee may answer, with the token its popup carries. Returns false when the invitee
 /// still holds an invite that has not expired. An older, unanswered invite is replaced.
 /// </summary>
    public bool TryAddInvite(uint inviteeCharId, uint guildId, uint inviterCharId, byte[] token, DateTime nowUtc, TimeSpan lifetime)
    {
        lock (_inviteGate)
        {
            if (_invites.TryGetValue(inviteeCharId, out var current) && nowUtc - current.CreatedUtc < lifetime)
                return false;
            _invites[inviteeCharId] = new GuildPendingInvite
            {
                GuildId = guildId, InviterCharId = inviterCharId, Token = (byte[])token.Clone(), CreatedUtc = nowUtc,
            };
            return true;
        }
    }

 /// <summary>
 /// Returns the invite the invitee has not answered yet, without taking it. Null when there is none.
 /// </summary>
    public GuildPendingInvite? PeekInvite(uint inviteeCharId)
        => _invites.TryGetValue(inviteeCharId, out var inv) ? inv : null;

    public GuildPendingInvite? TakeInvite(uint inviteeCharId)
        => _invites.TryRemove(inviteeCharId, out var inv) ? inv : null;

    
 /// <summary>Why <see cref="DepositAsync"/> did not move an item.</summary>
    public enum DepositRefusal
    {
 /// <summary>The item was moved.</summary>
        None,
 /// <summary>
 /// The item is no longer an owned bag item, the guild was disbanded, or the move failed and was undone.
 /// </summary>
        NotMoved,
 /// <summary>No free vault cell is below the guild's capacity.</summary>
        VaultFull,
    }

 /// <summary>
 /// Moves one item from a character's bag into the guild vault, into the lowest free cell below
 /// <paramref name="capacity"/>. The room check happens first. The item is removed from the bag before it is added to
 /// the vault, so it can never exist in both places. If the move fails, the item is restored to the bag.
 /// </summary>
 /// <returns>The stored record with its vault cell, and <see cref="DepositRefusal.None"/>, or null and the refusal.</returns>
    public Task<(GuildStorageItem? Stored, DepositRefusal Refusal)> DepositAsync(Guild g, InventoryItem item,
        IInventoryRepository inv, int capacity)
        
        
        
        
        
        
        => CharacterInventoryGate.RunAsync(item.CharacterId, () => DepositCoreAsync(g, item, inv, capacity));

    /// <summary>StorageType of a character's bag; only bag items can be deposited, and a withdrawn item returns to it.</summary>
    private const byte StorageBag = 0;

    /// <summary>Slot of an item not yet placed in a cell; the inventory store then assigns the lowest free bag cell.</summary>
    private const short UnplacedSlot = -1;

    private async Task<(GuildStorageItem? Stored, DepositRefusal Refusal)> DepositCoreAsync(Guild g, InventoryItem item,
        IInventoryRepository inv, int capacity)
    {
        
        var live = await inv.GetByIdAsync(item.Id);
        if (live == null || live.CharacterId != item.CharacterId || live.StorageType != StorageBag)
            return (null, DepositRefusal.NotMoved);

        lock (g.Gate)
        {
            if (!IsRegistered(g)) return (null, DepositRefusal.NotMoved);
            if (GuildVaultCapacity.LowestFreeCell(g.Storage, capacity) < 0) return (null, DepositRefusal.VaultFull);
        }

        var rec = new GuildStorageItem
        {
            Uid = live.Id, ItemId = live.ItemId, Count = live.Count, Refine = live.Refine,
            Durability = live.Durability, CardsJson = live.CardsJson, DepositedByCharId = live.CharacterId,
            Element = live.Element, DyeColor = live.DyeColor, SocketPunchersJson = live.SocketPunchersJson,
            DyeColor2 = live.DyeColor2, DyeColor3 = live.DyeColor3, FxEnchantIndex = live.FxEnchantIndex,
            EnchantReinforceId = live.EnchantReinforceId, EnchantSeconds = live.EnchantSeconds, Bound = live.Bound,
            AbilityValue = live.AbilityValue,
        };

        
        if (!await inv.RemoveItemAsync(live.Id, live.CharacterId))
            return (null, DepositRefusal.NotMoved);
        var full = false;
        try
        {
            lock (g.Gate)
            {
                
                
                if (!IsRegistered(g)) throw new InvalidOperationException($"guild {g.Id} was disbanded");
                var cell = GuildVaultCapacity.LowestFreeCell(g.Storage, capacity);
                if (cell < 0) full = true;
                else
                {
                    rec.StorageSlot = (byte)cell;
                    g.Storage.Add(rec);
                }
            }
            if (!full) await PersistAsync(g);                   
        }
        catch
        {
            await RestoreToBagAsync(g, live, rec, inv);
            return (null, DepositRefusal.NotMoved);
        }
        if (full)
        {
            
            await RestoreToBagAsync(g, live, rec, inv);
            return (null, DepositRefusal.VaultFull);
        }
        return (rec, DepositRefusal.None);
    }

 /// <summary>
 /// Compensation for <see cref="DepositAsync"/>: puts the item back into the bag on its original cell and removes it
 /// from the vault if it was added there.
 /// </summary>
    private static async Task RestoreToBagAsync(Guild g, InventoryItem live, GuildStorageItem rec, IInventoryRepository inv)
    {
        live.Id = rec.Uid;
        try { await inv.AddItemAsync(live); } catch {   }
        lock (g.Gate) { g.Storage.RemoveAll(x => x.Uid == rec.Uid); }
    }

 /// <summary>
 /// Moves one vault item back into a character's bag, by vault slot. The item is removed from the vault before it is
 /// added to the bag, keeping its uid. Returns the removed record, or null when the slot is empty.
 /// </summary>
    public async Task<GuildStorageItem?> WithdrawAsync(Guild g, byte storageSlot, uint toCharId, IInventoryRepository inv)
    {
        GuildStorageItem? rec;
        lock (g.Gate)
        {
            rec = g.Storage.FirstOrDefault(s => s.StorageSlot == storageSlot);
            if (rec != null) g.Storage.Remove(rec);
        }
        if (rec == null) return null;

        await PersistAsync(g);                                 
        try
        {
            var item = new InventoryItem
            {
                Id = rec.Uid, CharacterId = toCharId, ItemId = rec.ItemId, Count = rec.Count <= 0 ? 1 : rec.Count,
                Slot = UnplacedSlot, StorageType = StorageBag, Refine = rec.Refine, Durability = rec.Durability, CardsJson = rec.CardsJson,
                Element = rec.Element, DyeColor = rec.DyeColor, SocketPunchersJson = rec.SocketPunchersJson,
                DyeColor2 = rec.DyeColor2, DyeColor3 = rec.DyeColor3, FxEnchantIndex = rec.FxEnchantIndex,
                EnchantReinforceId = rec.EnchantReinforceId, EnchantSeconds = rec.EnchantSeconds, Bound = rec.Bound,
                AbilityValue = rec.AbilityValue,
            };
            await inv.AddItemAsync(item);                       
        }
        catch
        {
            
            lock (g.Gate) { g.Storage.Add(rec); }
            try { await PersistAsync(g); } catch {   }
            return null;
        }
        return rec;
    }

    private async Task PersistAsync(Guild g)
    {
        
        
        
        if (!IsRegistered(g)) return;
        try { if (_repo != null) await _repo.SaveAsync(g); }
        catch {   }
    }

 /// <summary>
 /// Persists the guild for a zeny move, and reports whether the write landed. False when the guild is no longer
 /// registered or the write fails, so the caller can undo the move. Without a store, the in-memory copy counts as
 /// written.
 /// </summary>
    private async Task<bool> TryPersistAsync(Guild g)
    {
        if (!IsRegistered(g)) return false;
        try
        {
            if (_repo != null) await _repo.SaveAsync(g);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
