namespace RO2.Guilds.Domain;

/// <summary>Why a character may not disband a guild. Each host handler maps it to its own wire code.</summary>
public enum DisbandRefusalReason
{
    /// <summary>The character may disband.</summary>
    None,
    /// <summary>The character is in no guild.</summary>
    NoGuild,
    /// <summary>The character is not the guild master.</summary>
    NotMaster,
    /// <summary>Another member is still in the guild.</summary>
    HasOtherMembers,
    /// <summary>The guild vault still holds items.</summary>
    VaultNotEmpty,
    /// <summary>The guild still holds zeny.</summary>
    FundsNotEmpty,
}

/// <summary>
/// The disband precondition: in a guild, the master, no other member, then an empty vault and no guild zeny.
/// The caller holds the guild's gate when the answer must stay true until the guild is removed.
/// </summary>
public static class GuildDisbandRules
{
    /// <summary>The first reason <paramref name="charId"/> may not disband <paramref name="g"/>, or None.</summary>
    public static DisbandRefusalReason DisbandRefusal(Guild? g, uint charId)
    {
        if (g == null) return DisbandRefusalReason.NoGuild;
        if (g.MasterCharId != charId) return DisbandRefusalReason.NotMaster;
        if (g.Members.Any(m => m.CharId != charId)) return DisbandRefusalReason.HasOtherMembers;
        if (g.Storage.Count > 0) return DisbandRefusalReason.VaultNotEmpty;
        if (g.Gold > 0) return DisbandRefusalReason.FundsNotEmpty;
        return DisbandRefusalReason.None;
    }
}
