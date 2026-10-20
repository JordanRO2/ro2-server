using System.Net;
using System.Net.Sockets;
using RO2.Identity.Application.Commands;
using RO2.Network.Throttling;

namespace RO2.Server.Login.Services;

/// <summary>
/// S-H3: the throttle in front of RMI 12002. Every attempt costs a BCrypt-11 hash (about 100 ms of CPU), so
/// without a limit one client can burn the CPU or brute-force a password. This gate runs in the Login layer
/// BEFORE <see cref="AuthenticateCommandHandler"/> (the Identity project stays free of any network dependency)
/// and refuses without touching the hasher.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Per IP: <see cref="MaxAttemptsPerIp"/> attempts per <see cref="IpWindowMs"/> (10 / 5 min). Every 12002
/// counts, successes too, and a success never clears it, so a valid account cannot be used to reset the budget.
/// IPv6 clients are keyed by their /64 (see <see cref="KeyFor"/>).</item>
/// <item>Per account name (case-insensitive): <see cref="MaxAttemptsPerAccount"/> attempts per
/// <see cref="AccountWindowMs"/> (5 / 5 min), cleared by a successful login. It stops a distributed guess at one
/// account; the price is that someone can lock a known name for the window, which only costs the owner a
/// retry in 5 minutes.</item>
/// <item>A refused attempt gets the ordinary failure reply (1004), never a distinct "locked" code, and no delay:
/// a sleeping handler would only hold a task per attempt.</item>
/// </list>
/// Time is passed in as milliseconds of a monotonic clock so tests are deterministic.
/// </remarks>
public sealed class LoginThrottle
{
    /// <summary>Attempts admitted per IP key per <see cref="IpWindowMs"/>.</summary>
    public const int MaxAttemptsPerIp = 10;
    /// <summary>Per-IP window, 5 minutes.</summary>
    public const long IpWindowMs = 5 * 60 * 1000;
    /// <summary>Attempts admitted per account name per <see cref="AccountWindowMs"/>.</summary>
    public const int MaxAttemptsPerAccount = 5;
    /// <summary>Per-account window, 5 minutes.</summary>
    public const long AccountWindowMs = 5 * 60 * 1000;

    private readonly SlidingWindowRateLimiter<string> _perIp = new(MaxAttemptsPerIp, IpWindowMs, StringComparer.Ordinal);
    private readonly SlidingWindowRateLimiter<string> _perAccount = new(MaxAttemptsPerAccount, AccountWindowMs, StringComparer.OrdinalIgnoreCase);

    /// <summary>The clock the handler feeds in: milliseconds of the process's monotonic clock.</summary>
    public static long NowMs => Environment.TickCount64;

    /// <summary>The throttle key for a peer: IPv4 (or IPv4-mapped) as is, IPv6 reduced to its /64, "unknown" without an address.</summary>
    public static string KeyFor(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// Authenticates <paramref name="command"/> for a client at <paramref name="ipKey"/>, or refuses it without
    /// calling <paramref name="handler"/> (so without a BCrypt hash) when the IP or the account
    /// budget is spent at <paramref name="nowMs"/>.
    /// </summary>
    public async Task<AuthenticateResult> AuthenticateAsync(
        string ipKey,
        AuthenticateCommand command,
        AuthenticateCommandHandler handler,
        long nowMs,
        CancellationToken ct = default)
    {
        if (!_perIp.TryAcquire(ipKey, nowMs) || !_perAccount.TryAcquire(command.Username, nowMs))
            return Refused(command, AuthenticateFailure.WrongPassword);

        var result = await handler.HandleAsync(command, ct).ConfigureAwait(false);
        if (result.Succeeded)
            _perAccount.Reset(command.Username);
        return result;
    }

    private static AuthenticateResult Refused(AuthenticateCommand command, AuthenticateFailure failure)
        => new(false, 0, command.Username, false, false, failure);
}
