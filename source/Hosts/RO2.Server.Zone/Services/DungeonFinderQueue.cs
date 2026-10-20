using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The dungeon finder queue: one entry per searching player, holding the lists the search named and the role
/// flags, in the order they queued. It backs the 2806 search state, the solo match announced by 2808, and the
/// 1558 cancel.
/// </summary>
/// <remarks>
/// <para>
/// Every member of a list is told the list's current state whenever the queue for that list changes.
/// </para>
/// <para>
/// Members take the list's tank, healer and dealer slots in queue order. A member whose flags allow more than
/// one role takes the first free one in tank, healer, dealer order. A member with no free slot is shown role 0.
/// </para>
/// <para>
/// A list with a single-player party size is matched as soon as the searcher fills it alone. Party-size lists
/// are queued and counted but not matched by this server.
/// </para>
/// <para>
/// Entries are removed by a 1558 cancel, or by <see cref="Prune"/> once the player's session is gone.
/// </para>
/// </remarks>
public sealed class DungeonFinderQueue
{
 /// <summary>The queue shared by the Zone handlers.</summary>
    public static DungeonFinderQueue Shared { get; } = new();

 /// <summary>Filled slots per role.</summary>
    public readonly record struct RoleCounts(byte Tanks, byte Healers, byte Dealers)
    {
        public int Total => Tanks + Healers + Dealers;
    }

 /// <summary>One searching player. <see cref="ListIds"/>[0] is the list named in its 2806.</summary>
    public sealed class Entry(PlayerState player, uint sessionId, uint[] listIds, byte roleFlags)
    {
        public PlayerState Player { get; } = player;
        public uint SessionId { get; } = sessionId;
        public IReadOnlyList<uint> ListIds { get; } = listIds;
        public byte RoleFlags { get; } = roleFlags;
        public uint PrimaryListId => ListIds[0];
    }

 /// <summary>A solo match formed at join: the list, the searcher's role in it, and the group counts.</summary>
    public sealed record Match(uint ListId, byte Role, RoleCounts Group);

 /// <summary>
 /// The result of a 1555 on the queue. <see cref="AlreadyQueued"/> leaves the queue unchanged. Otherwise the
 /// searcher is told the shown list, slots and own role in its 2806, and when a <see cref="Match"/> is present
 /// the searcher has already left the queue.
 /// </summary>
    public sealed record JoinOutcome(bool AlreadyQueued, Entry? Entry, uint ShownListId, RoleCounts Shown, byte OwnRole, Match? Match);

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

 /// <summary>
 /// Queues <paramref name="player"/> for <paramref name="rows"/> with <paramref name="roleFlags"/>, and matches it
 /// at once to the first single-player list it fills alone.
 /// </summary>
    public JoinOutcome Join(PlayerState player, byte roleFlags, IReadOnlyList<FindDungeonList> rows)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rows.Count);
        lock (_gate)
        {
            if (_entries.Exists(e => ReferenceEquals(e.Player, player)))
                return new JoinOutcome(true, null, 0, default, 0, null);

            var entry = new Entry(player, player.SessionId, rows.Select(r => (uint)r.List_ID).ToArray(), roleFlags);
            foreach (var row in rows)
            {
                if (row.Party_Num != 1) continue;
                var (alone, roles) = Assign(row, [entry]);
                var role = roles[entry];
                if (role != 0 && alone.Total == row.Party_Num)
                    return new JoinOutcome(false, entry, row.List_ID, alone, role, new Match(row.List_ID, role, alone));
            }

            _entries.Add(entry);
            var primary = rows[0];
            var (filled, assigned) = Assign(primary, MembersLocked(primary.List_ID));
            return new JoinOutcome(false, entry, primary.List_ID, filled, assigned[entry], null);
        }
    }

 /// <summary>Removes the player's search and returns it, or null when it had none.</summary>
    public Entry? Leave(PlayerState player)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => ReferenceEquals(e.Player, player));
            if (i < 0) return null;
            var entry = _entries[i];
            _entries.RemoveAt(i);
            return entry;
        }
    }

 /// <summary>Drops every entry selected by <paramref name="isGone"/> (closed sessions) and returns them.</summary>
    public List<Entry> Prune(Func<Entry, bool> isGone)
    {
        lock (_gate)
        {
            var gone = _entries.FindAll(e => isGone(e));
            if (gone.Count > 0) _entries.RemoveAll(e => isGone(e));
            return gone;
        }
    }

 /// <summary>The members searching <paramref name="listId"/>, in queue order.</summary>
    public List<Entry> MembersOf(uint listId)
    {
        lock (_gate) return MembersLocked(listId);
    }

    private List<Entry> MembersLocked(uint listId) => _entries.FindAll(e => e.ListIds.Contains(listId));

 /// <summary>
 /// Assigns each of <paramref name="members"/>, in queue order, a slot of the list's role quotas and returns the
 /// filled counts and each member's role (0 when no slot is free).
 /// </summary>
    public static (RoleCounts Filled, Dictionary<Entry, byte> Roles) Assign(FindDungeonList row, IReadOnlyList<Entry> members)
    {
        byte tanks = 0, healers = 0, dealers = 0;
        var roles = new Dictionary<Entry, byte>(ReferenceEqualityComparer.Instance);
        foreach (var e in members)
        {
            byte role = 0;
            if ((e.RoleFlags & DungeonFinderPackets.RoleTank) != 0 && tanks < row.Role_Tanker) { role = DungeonFinderPackets.RoleTank; tanks++; }
            else if ((e.RoleFlags & DungeonFinderPackets.RoleHealer) != 0 && healers < row.Role_Healer) { role = DungeonFinderPackets.RoleHealer; healers++; }
            else if ((e.RoleFlags & DungeonFinderPackets.RoleDealer) != 0 && dealers < row.Role_Dealer) { role = DungeonFinderPackets.RoleDealer; dealers++; }
            roles[e] = role;
        }
        return (new RoleCounts(tanks, healers, dealers), roles);
    }

 /// <summary>
 /// Sends a fresh 2806 to every member searching one of <paramref name="listIds"/>, except
 /// <paramref name="skip"/>.
 /// </summary>
    public async Task PushStateAsync(IEnumerable<uint> listIds, PlayerState? skip, Func<uint, FindDungeonList?> rowOf,
        Func<uint, byte[], Task> sendToSession)
    {
        foreach (var listId in listIds.Distinct())
        {
            if (rowOf(listId) is not { } row) continue;
            var members = MembersOf(listId);
            var (filled, roles) = Assign(row, members);
            foreach (var m in members)
            {
                if (m.PrimaryListId != listId || ReferenceEquals(m.Player, skip)) continue;
                await sendToSession(m.SessionId, DungeonFinderPackets.BuildInProgressInfo2806(listId, filled, roles[m]));
            }
        }
    }

 /// <summary>
 /// Drops the entries of players that are no longer registered under their session, and sends the updated state
 /// to the remaining members of their lists.
 /// </summary>
    public async Task PruneAndPushAsync(PlayerStateManager players, Func<uint, FindDungeonList?> rowOf,
        Func<uint, byte[], Task> sendToSession)
    {
        var gone = Prune(e => !ReferenceEquals(players.GetPlayer(e.SessionId), e.Player));
        if (gone.Count > 0)
            await PushStateAsync(gone.SelectMany(e => e.ListIds), null, rowOf, sendToSession);
    }

 /// <summary>Returns the Find_Dungeon_List row for a List_ID from the loaded tables.</summary>
    public static Func<uint, FindDungeonList?> RowsOf(GameDataStore data)
        => id => data.FindDungeonList.FirstOrDefault(r => r.List_ID == id);
}
