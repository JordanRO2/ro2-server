using RO2.BuildingBlocks.Bus;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Application.IntegrationEvents;
using RO2.Identity.Domain.Account;
using RO2.Identity.Domain.Events;

namespace RO2.Identity.Application.Commands;

/// <summary>
/// Handles <see cref="AuthenticateCommand"/>, reproducing <c>LoginHandler</c>'s auth flow, orchestrating the
/// <see cref="Account"/> aggregate:
/// <list type="number">
/// <item>lookup by username (<see cref="IAccountRepository.UsernameExistsAsync"/>);</item>
/// <item><b>unknown username</b>: the result is <see cref="AuthenticateFailure.UnknownAccount"/>, which the
/// gateway answers with 1003 "account not registered". Auto-registration was removed — a login attempt never
/// creates an account;</item>
/// <item><b>validate branch</b> (existing): <see cref="Account.Authenticate"/> (BCrypt verify + ban check);
/// on failure the result carries the reason (<see cref="AuthenticateFailure.WrongPassword"/> or
/// <see cref="AuthenticateFailure.Banned"/>), which the gateway answers with 1004 or 1005; on success
/// <c>UpdateAsync</c> (drains last-login).</item>
/// </list>
/// After persistence, the aggregate's domain events are mapped to integration events and published via
/// <see cref="IEventBus"/> (the outbox relay does this in production; the in-memory bus does it inline here).
/// </summary>
public sealed class AuthenticateCommandHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly IEventBus _bus;

    /// <summary>Creates the handler.</summary>
    /// <param name="accounts">The account repository (pure persistence + id allocation).</param>
    /// <param name="hasher">The BCrypt password hasher.</param>
    /// <param name="clock">The UTC clock.</param>
    /// <param name="bus">The integration-event bus.</param>
    public AuthenticateCommandHandler(
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
    /// <param name="command">The authenticate command (wire-decoded username/password).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The <see cref="AuthenticateResult"/> the gateway uses to build its reply.</returns>
    public async Task<AuthenticateResult> HandleAsync(
        AuthenticateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A password over BCrypt's 72-byte input would verify (or register) on its first 72 bytes alone. Answer it
        // like a wrong password, before any store or hasher work.
        if (!Credentials.IsPasswordLengthAcceptable(command.Password))
        {
            return new AuthenticateResult(false, 0, command.Username, false, false, AuthenticateFailure.WrongPassword);
        }

        // Unknown username — answered 1003 "account not registered". Auto-registration was removed, so a login
        // attempt never creates an account.
        if (!await _accounts.UsernameExistsAsync(command.Username, cancellationToken).ConfigureAwait(false))
        {
            return new AuthenticateResult(false, 0, command.Username, false, false, AuthenticateFailure.UnknownAccount);
        }

        // Validate branch — existing account: BCrypt verify + ban check inside the aggregate.
        var existing = await _accounts.GetByUsernameAsync(command.Username, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            // Mirrors ValidateLoginAsync returning null when the account vanished between the
            // exists-check and the load (or no registration attempt could be stored): the gateway sends
            // WrongPassword (1004).
            return new AuthenticateResult(false, 0, command.Username, false, false, AuthenticateFailure.WrongPassword);
        }

        var account = existing;
        var result = account.Authenticate(command.Password, _hasher, _clock);

        if (!result.Succeeded)
        {
            // Wrong password (1004) or active ban (1005): the gateway tells them apart by the reason.
            // Persist so the AuthenticationFailed domain event reaches the bus (observability only).
            await PublishDomainEventsAsync(account, cancellationToken).ConfigureAwait(false);
            var failure = result.FailureReason == AuthenticationFailureReason.Banned
                ? AuthenticateFailure.Banned
                : AuthenticateFailure.WrongPassword;
            return new AuthenticateResult(false, 0, command.Username, false, false, failure);
        }

        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);
        await PublishDomainEventsAsync(account, cancellationToken).ConfigureAwait(false);
        return Succeeded(account, result);
    }

    private static AuthenticateResult Succeeded(Account account, AuthenticationResult result) => new(
        true,
        account.Id.Value,
        account.Username,
        result.HasSecondPassword,
        result.WasAutoRegistered);

    /// <summary>
    /// True when <paramref name="username"/> is a well-formed account login: not blank, at most
    /// <see cref="Credentials.MaxUsernameLength"/> characters, no control character (CR/LF included) and no
    /// leading or trailing whitespace. Used by <c>LoginThrottle</c> to tell a real login attempt apart from a
    /// malformed one; the handler no longer registers, so it no longer calls this.
    /// </summary>
    /// <param name="username">The wire-decoded login key.</param>
    /// <returns><see langword="true"/> when the name is a well-formed login key.</returns>
    public static bool IsRegistrableUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > Credentials.MaxUsernameLength)
        {
            return false;
        }

        if (char.IsWhiteSpace(username[0]) || char.IsWhiteSpace(username[^1]))
        {
            return false;
        }

        foreach (var c in username)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private async Task PublishDomainEventsAsync(Account account, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in account.DomainEvents)
        {
            switch (domainEvent)
            {
                case AccountRegistered e:
                    await _bus.PublishAsync(
                        new AccountRegisteredIntegrationEvent(e.EventId, e.OccurredOn, e.AccountId.Value, e.Username),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case AccountAuthenticated e:
                    await _bus.PublishAsync(
                        new AccountAuthenticatedIntegrationEvent(
                            e.EventId, e.OccurredOn, e.AccountId.Value, e.Username,
                            e.HasSecondPassword, e.WasAutoRegistered),
                        cancellationToken).ConfigureAwait(false);
                    break;

                // AuthenticationFailed stays an in-process domain event (server-side observability only);
                // it intentionally has no cross-service integration-event mapping.
                default:
                    break;
            }
        }

        account.ClearDomainEvents();
    }
}
