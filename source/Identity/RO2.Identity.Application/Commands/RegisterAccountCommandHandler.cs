using RO2.BuildingBlocks.Bus;
using RO2.Identity.Application.IntegrationEvents;
using RO2.Identity.Domain.Account;
using RO2.Identity.Domain.Events;

namespace RO2.Identity.Application.Commands;

/// <summary>
/// Handles <see cref="RegisterAccountCommand"/>: allocates an id, creates the account, persists it, and publishes
/// the registration event. Uses the same registration path as <see cref="AuthenticateCommandHandler"/>.
/// </summary>
public sealed class RegisterAccountCommandHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly IEventBus _bus;

 /// <summary>Creates the handler.</summary>
 /// <param name="accounts">The account repository.</param>
 /// <param name="hasher">The password hasher.</param>
 /// <param name="clock">The UTC clock.</param>
 /// <param name="bus">The integration event bus.</param>
    public RegisterAccountCommandHandler(
        IAccountRepository accounts,
        IPasswordHasher hasher,
        IClock clock,
        IEventBus bus)
    {
        _accounts = accounts;
        _hasher = hasher;
        _clock = clock;
        _bus = bus;
    }

 /// <summary>Executes the command.</summary>
 /// <param name="command">The register command.</param>
 /// <param name="cancellationToken">A token to cancel the operation.</param>
 /// <returns>The new account's id and login key.</returns>
 /// <exception cref="DomainException">Thrown when the username already exists.</exception>
    public async Task<RegisterAccountResult> HandleAsync(
        RegisterAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await _accounts.UsernameExistsAsync(command.Username, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException($"username '{command.Username}' already exists");
        }

        var id = await _accounts.NextIdAsync(cancellationToken).ConfigureAwait(false);
        var account = Account.Register(id, command.Username, command.Password, _hasher, _clock);
        await _accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

        foreach (var domainEvent in account.DomainEvents)
        {
            if (domainEvent is AccountRegistered e)
            {
                await _bus.PublishAsync(
                    new AccountRegisteredIntegrationEvent(e.EventId, e.OccurredOn, e.AccountId.Value, e.Username),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        account.ClearDomainEvents();

        return new RegisterAccountResult(account.Id.Value, account.Username);
    }
}
