using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>Why a funds move did not change the guild's funds.</summary>
public enum FundsRefusal
{
    /// <summary>The funds changed.</summary>
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
/// The guild-side steps of a funds move: each takes the guild's gate, applies the pure funds rule, writes the guild
/// and undoes the change in memory when the write fails. The live registry implements it in the host.
/// </summary>
public interface IGuildFundsLedger
{
    /// <summary>Adds <paramref name="amount"/> unless the result passes <paramref name="limit"/>; written before it returns.</summary>
    Task<(FundsRefusal Refusal, long FundsAfter)> AddAsync(Guild guild, long amount, long limit);

    /// <summary>Takes <paramref name="amount"/> unless the guild holds less; written before it returns.</summary>
    Task<(FundsRefusal Refusal, long FundsAfter)> TakeAsync(Guild guild, long amount);

    /// <summary>Returns <paramref name="amount"/> with no limit check and writes the guild; gives the funds after.</summary>
    Task<long> ReturnAsync(Guild guild, long amount);
}
