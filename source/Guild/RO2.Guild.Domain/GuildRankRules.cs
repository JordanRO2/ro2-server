namespace RO2.Guilds.Domain;

/// <summary>
/// Pure rules of a guild's rank table: the default titles, who holds which permission, and the load-time repair of a
/// stored table. Each rule takes the guild's own gate, so callers need no lock of their own.
/// </summary>
public static class GuildRankRules
{
    /// <summary>Rank index of the master. Only the master holds it, and it changes hands only by handover.</summary>
    public const byte MasterRankIndex = 0;

    /// <summary>Rank index of the member title in a default two-title table.</summary>
    public const byte DefaultMemberRankIndex = 1;

    /// <summary>
    /// Display name of the default master rank. Custom content (not a 1:1 of retail, which sent the raw client string
    /// id "11832" = "master"); a readable name sent verbatim, kept within the 10-character rank-name cap.
    /// </summary>
    public const string DefaultMasterTitle = "Master";

    /// <summary>Display name of the default member rank. Custom content (retail's raw string id was "11833" = "member").</summary>
    public const string DefaultMemberTitle = "Member";

    /// <summary>The raw client string-id rank titles older guilds were stored with; rewritten to the readable defaults on load.</summary>
    private const string LegacyMasterTitle = " 11832";
    private const string LegacyMemberTitle = " 11833";

    /// <summary>Permission byte with every one of the four rights (invite, remove member, storage, notice) set.</summary>
    public const byte PermissionsAll = 0x0F;

    /// <summary>Permission byte with no right set.</summary>
    public const byte PermissionsNone = 0x00;

    /// <summary>
    /// Permission bit to remove members. Bit 0 is the invite permission, and the bits follow the order of the client's
    /// permission columns: invite, remove member, storage, notice.
    /// </summary>
    public const byte PermissionKick = 0x02;

    /// <summary>Permission bit to invite members.</summary>
    public const byte PermissionInvite = 0x01;

    /// <summary>Maximum number of titles a rank table may hold, indices 0 to 9.</summary>
    public const int MaxRanks = 10;

    /// <summary>Minimum number of titles a rank table keeps.</summary>
    public const int MinRanks = 2;

    /// <summary>The rank table of a newly created guild: a master title with all permissions and a member title with none.</summary>
    public static List<GuildRank> DefaultRanks() => new()
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
    public static bool NormalizeRanks(Guild g)
    {
        lock (g.Gate)
        {
            var changed = false;
            if (g.Ranks == null) g.Ranks = new List<GuildRank>();
            var defaults = DefaultRanks();

            // Migrate guilds stored with the old raw string-id titles to the readable defaults.
            foreach (var r in g.Ranks)
            {
                if (r.Name == LegacyMasterTitle) { r.Name = DefaultMasterTitle; changed = true; }
                else if (r.Name == LegacyMemberTitle) { r.Name = DefaultMemberTitle; changed = true; }
            }


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
}
