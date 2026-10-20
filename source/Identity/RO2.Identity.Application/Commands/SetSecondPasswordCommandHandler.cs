using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;

namespace RO2.Identity.Application.Commands;

/// <summary>
/// Handles <see cref="SetSecondPasswordCommand"/>: loads the account, stores a hash of the PIN, and persists it.
/// Returns <see cref="SetSecondPasswordResult.AccountFound"/> <see langword="false"/> when the account does not exist.
/// </summary>
public sealed class SetSecondPasswordCommandHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _hasher;

 /// <param name="accounts">The account repository.</param>
 /// <param name="hasher">The password hasher.</param>
    public SetSecondPasswordCommandHandler(IAccountRepository accounts, IPasswordHasher hasher)
    {
        _accounts = accounts;
        _hasher = hasher;
    }

 /// <summary>Executes the command.</summary>
 /// <param name="command">The set-PIN command.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>The result of the operation.</returns>
    public async Task<SetSecondPasswordResult> HandleAsync(
        SetSecondPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await _accounts.GetByIdAsync(new AccountId(command.AccountId), cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return new SetSecondPasswordResult(false);
        }

        account.SetSecondPassword(command.Pin, _hasher);
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return new SetSecondPasswordResult(true);
    }
}
