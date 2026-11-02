using RO2.BuildingBlocks;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>How a guild funds move ended; the host maps it to the 2672 / 2673 result code.</summary>
public enum FundsOutcome
{
    /// <summary>The move completed.</summary>
    Ok,

    /// <summary>Withdraw only: the guild funds were taken but the wallet write failed and the zeny was already spent, so the move stands.</summary>
    OkWalletUnsaved,

    /// <summary>Deposit only: the wallet holds less than the amount.</summary>
    WalletShort,

    /// <summary>The wallet write failed; the move was undone (deposit: nothing moved; withdraw: the funds were given back).</summary>
    WalletWriteFailed,

    /// <summary>Deposit only: the funds would pass the storage limit.</summary>
    OverLimit,

    /// <summary>Withdraw only: the guild holds less than the amount.</summary>
    NotEnoughFunds,

    /// <summary>The guild was disbanded before the move.</summary>
    NotRegistered,

    /// <summary>The guild write failed and the move was undone.</summary>
    GuildWriteFailed,
}

/// <summary>
/// The result of a funds move. <see cref="Error"/> is the wallet-write exception to log, if any;
/// <see cref="RefundError"/> is set when the wallet refund after a refused deposit did not persist.
/// </summary>
public readonly record struct FundsMove(FundsOutcome Outcome, long FundsAfter, Exception? Error = null, Exception? RefundError = null);

/// <summary>
/// Sequences a guild funds deposit or withdrawal between the live wallet and the guild's funds, with the undo on a
/// failed write. Deposit: wallet debited and written first, then the funds added, the wallet credited back on a
/// refusal. Withdraw: funds taken and written first, then the wallet credited and written, the funds given back
/// on a wallet failure. The host keeps the request checks, the wire sends and the storage log.
/// </summary>
public sealed class GuildFundsUseCase(IGuildFundsLedger ledger)
{
    /// <summary>Moves <paramref name="amount"/> from the wallet to the guild funds. <paramref name="saveWallet"/> writes the wallet balance as it is when called.</summary>
    public async Task<FundsMove> DepositAsync(Guild guild, IWallet wallet, long amount, long limit, Func<Task> saveWallet)
    {
        if (!wallet.TryDebit(amount)) return new(FundsOutcome.WalletShort, 0);
        try
        {
            await saveWallet();
        }
        catch (Exception ex)
        {
            wallet.Credit(amount);
            return new(FundsOutcome.WalletWriteFailed, 0, ex);
        }

        var (refusal, fundsAfter) = await ledger.AddAsync(guild, amount, limit);
        if (refusal == FundsRefusal.None) return new(FundsOutcome.Ok, fundsAfter);

        wallet.Credit(amount);
        Exception? refundError = null;
        try
        {
            await saveWallet();
        }
        catch (Exception ex)
        {
            refundError = ex;
        }
        var outcome = refusal switch
        {
            FundsRefusal.OverLimit => FundsOutcome.OverLimit,
            FundsRefusal.NotRegistered => FundsOutcome.NotRegistered,
            _ => FundsOutcome.GuildWriteFailed,
        };
        return new(outcome, 0, RefundError: refundError);
    }

    /// <summary>Moves <paramref name="amount"/> from the guild funds to the wallet. <paramref name="saveWallet"/> writes the wallet balance as it is when called.</summary>
    public async Task<FundsMove> WithdrawAsync(Guild guild, IWallet wallet, long amount, Func<Task> saveWallet)
    {
        var (refusal, fundsAfter) = await ledger.TakeAsync(guild, amount);
        switch (refusal)
        {
            case FundsRefusal.None: break;
            case FundsRefusal.NotEnoughFunds: return new(FundsOutcome.NotEnoughFunds, 0);
            case FundsRefusal.NotRegistered: return new(FundsOutcome.NotRegistered, 0);
            default: return new(FundsOutcome.GuildWriteFailed, 0);
        }

        wallet.Credit(amount);
        try
        {
            await saveWallet();
        }
        catch (Exception ex)
        {
            // The zeny may already be spent: if it cannot be taken back the withdrawal stands; otherwise give the funds back.
            if (!wallet.TryDebit(amount)) return new(FundsOutcome.OkWalletUnsaved, fundsAfter, ex);
            await ledger.ReturnAsync(guild, amount);
            return new(FundsOutcome.WalletWriteFailed, 0, ex);
        }
        return new(FundsOutcome.Ok, fundsAfter);
    }
}
