using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;

namespace RO2.Identity.Application.Commands;

/// <summary>
/// Handles <see cref="VerifySecondPasswordCommand"/>. Verifies the submitted PIN when one is set; otherwise stores it.
/// Returns <see cref="VerifySecondPasswordResult.AccountFound"/> <see langword="false"/> when the account does not exist.
/// </summary>
public sealed class VerifySecondPasswordCommandHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _hasher;

 /// <summary>Creates the handler.</summary>
 /// <param name="accounts">The account repository.</param>
 /// <param name="hasher">The password hasher.</param>
    public VerifySecondPasswordCommandHandler(IAccountRepository accounts, IPasswordHasher hasher)
    {
        _accounts = accounts;
        _hasher = hasher;
    }

 /// <summary>Executes the command.</summary>
 /// <param name="command">The verify-PIN command.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>The result the gateway uses to build its reply.</returns>
    public async Task<VerifySecondPasswordResult> HandleAsync(
        VerifySecondPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await _accounts.GetByIdAsync(new AccountId(command.AccountId), cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return new VerifySecondPasswordResult(false, false, false);
        }

        if (account.HasSecondPassword)
        {
            var ok = account.VerifySecondPassword(command.Pin, _hasher);
            return new VerifySecondPasswordResult(true, true, WrongPin: !ok);
        }

        account.SetSecondPassword(command.Pin, _hasher);
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);
        return new VerifySecondPasswordResult(true, false, false);
    }
}
