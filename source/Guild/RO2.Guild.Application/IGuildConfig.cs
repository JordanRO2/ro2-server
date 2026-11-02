using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The Config_Def numbers (and per-guild limits derived from them) the guild flows need, behind a port so the
/// flows do not reach the host GameDataStore. A missing or unparseable row yields the neutral value noted on each member.
/// </summary>
public interface IGuildConfig
{
    /// <summary>The creation tax (Config_Def type 2); 0 (no tax) when the row is absent.</summary>
    long CreateFee { get; }

    /// <summary>The wait before a character who left a guild may join another (type 85, hours); zero when absent.</summary>
    TimeSpan RejoinCooldown { get; }

    /// <summary>How long a pending guild invite lives (type 5, seconds); zero when absent.</summary>
    TimeSpan InviteLifetime { get; }

    /// <summary>The funds a guild can store given its learned skills (guild-storage-limit skill category); 0 when none learned.</summary>
    long FundsLimit(Guild guild);

    /// <summary>History retention in days (type 15); 0 (no bound) when absent.</summary>
    int HistoryRetentionDays { get; }

    /// <summary>Lines per storage/funds history page (type 14); the client's 13-line default when absent.</summary>
    int HistoryPageSize { get; }
}
