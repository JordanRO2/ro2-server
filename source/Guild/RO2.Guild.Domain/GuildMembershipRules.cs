namespace RO2.Guilds.Domain;

/// <summary>
/// Pure membership transitions of a guild: join, leave or kick, rank change and handover of the master title. Each
/// transition mutates the guild it is given and reports the result. The caller holds the guild's gate, the member
/// index, the persistence and the wire.
/// </summary>
public static class GuildMembershipRules
{
    /// <summary>Returns the member whose GID is <paramref name="gid"/>, or null.</summary>
    public static GuildMember? FindByGid(Guild g, ulong gid) => g.Members.FirstOrDefault(m => m.Gid == gid);

    /// <summary>
    /// Adds the character as a member on the lowest rank, stamped with <paramref name="nowUtc"/>, unless it already is
    /// one. A level of 0 is stored as <see cref="GuildMember.MinLevel"/>. Returns the member, new or existing.
    /// </summary>
    public static GuildMember Join(Guild g, uint charId, ulong gid, string name, byte level, byte job, DateTime nowUtc)
    {
        var m = g.Members.FirstOrDefault(x => x.CharId == charId);
        if (m == null)
        {
            m = new GuildMember
            {
                CharId = charId, Gid = gid, Name = name,
                Level = level == 0 ? GuildMember.MinLevel : level, Job = job, AuthId = GuildRankRules.LowestRankIndex(g),
                JoinedUtc = nowUtc,
            };
            g.Members.Add(m);
        }
        return m;
    }

    /// <summary>Removes the member with <paramref name="charId"/> and returns it, or null when there is none.</summary>
    public static GuildMember? Leave(Guild g, uint charId)
    {
        var removed = g.Members.FirstOrDefault(x => x.CharId == charId);
        if (removed != null) g.Members.Remove(removed);
        return removed;
    }

    /// <summary>
    /// Gives <paramref name="m"/> the title <paramref name="authId"/>. Refused, with the guild unchanged, for the
    /// master, for the master's title and for a title the table does not hold.
    /// </summary>
    public static bool TrySetAuth(Guild g, GuildMember m, byte authId)
    {
        if (m.CharId == g.MasterCharId || authId == GuildRankRules.MasterRankIndex || g.Ranks.All(r => r.Index != authId))
            return false;
        m.AuthId = authId;
        return true;
    }

    /// <summary>
    /// Hands the master title to <paramref name="m"/>, who takes rank 0. The old master, if still a member and not
    /// <paramref name="m"/>, drops to the lowest rank.
    /// </summary>
    public static void SetMaster(Guild g, GuildMember m)
    {
        var old = g.Members.FirstOrDefault(x => x.CharId == g.MasterCharId);
        if (old != null && !ReferenceEquals(old, m)) old.AuthId = GuildRankRules.LowestRankIndex(g);
        g.MasterCharId = m.CharId;
        m.AuthId = GuildRankRules.MasterRankIndex;
    }
}
