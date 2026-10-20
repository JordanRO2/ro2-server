using RO2.BuildingBlocks.Domain;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// Persistence port for the <see cref="Account"/> aggregate.
/// </summary>
/// <remarks>
/// Extends <see cref="IRepository{TAggregate,TId}"/> with lookup by username and account id allocation.
/// </remarks>
public interface IAccountRepository : IRepository<Account, AccountId>
{
 /// <summary>Loads the account with the given username, or <see langword="null"/> if none exists.</summary>
 /// <param name="username">The login key.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>The account, or <see langword="null"/>.</returns>
    Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

 /// <summary>Whether an account with the given username exists (the auto-register decision).</summary>
 /// <param name="username">The login key.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns><see langword="true"/> if the username is taken; otherwise <see langword="false"/>.</returns>
    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default);

 /// <summary>Allocates the next account id.</summary>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>The next account id to assign.</returns>
    Task<AccountId> NextIdAsync(CancellationToken cancellationToken = default);
}
