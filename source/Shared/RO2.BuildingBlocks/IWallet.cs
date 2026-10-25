namespace RO2.BuildingBlocks;

/// <summary>
/// The player's zeny wallet as a port: the single authoritative balance and its atomic mutators. Implemented by
/// the runtime player state; economy and other spenders depend on this contract rather than the concrete player
/// object, so the wallet can later move into its own bounded context without touching every call site.
/// </summary>
public interface IWallet
{
    /// <summary>The authoritative balance. Never negative.</summary>
    long Gold { get; }

    /// <summary>The balance held in the bank.</summary>
    long BankGold { get; }

    /// <summary>Takes <paramref name="amount"/> atomically if the wallet can pay it; false (unchanged) otherwise.</summary>
    bool TryDebit(long amount);

    /// <summary>Takes up to <paramref name="amount"/> atomically and returns what it took (a deliberate clamp).</summary>
    long DebitUpTo(long amount);

    /// <summary>Adds <paramref name="amount"/> atomically; the balance saturates rather than wrapping.</summary>
    void Credit(long amount);
}
