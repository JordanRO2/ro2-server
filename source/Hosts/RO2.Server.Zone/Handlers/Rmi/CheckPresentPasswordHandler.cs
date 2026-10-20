using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.Throttling;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1605 ReqCheckPresentPassword - Verify gift password</summary>
/// <remarks>
/// <para>
/// WIRE. The body is one Proud WStr, <c>[u8 widthTag][CompactLen nChars][nChars * widthTag bytes]</c>
/// (read with <see cref="ProudWStr"/>). A typical frame is 11 bytes, <c>02 01 04</c> + four UTF-16
/// characters, e.g. '1234' or '0000'.
/// </para>
/// <para>
/// ANSWER 2891 AnsCheckPresentPassword is a bare u16 result; 0 is success.
/// </para>
/// <para>
/// The entered password is checked against the account's password stored by 1600
/// (<see cref="IPresentPasswordRepository"/>, per account). A match answers 0. A mismatch answers
/// <see cref="ResultWrongPassword"/>, a code chosen by this server (the same 1 that SetPresentPasswordHandler
/// uses for its refusals). An account with no stored password answers 0: there is nothing the
/// entry can fail against, and 1599 must say the same thing from the same store
/// (<see cref="IPresentPasswordRepository.HasPasswordAsync"/>). A store
/// failure is treated as a mismatch, so a database outage never opens the gift path.
/// </para>
/// </remarks>
public class CheckPresentPasswordHandler : ZoneRmiHandlerBase<CheckPresentPasswordHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCheckPresentPassword;
    public override string Name => "CheckPresentPassword";
    public CheckPresentPasswordHandler(IServiceScopeFactory s, ILogger<CheckPresentPasswordHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Refusal code for a wrong password (see remarks).</summary>
    private const ushort ResultWrongPassword = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        int pos = 0;
        if (!ProudWStr.TryRead(payload.Span, ref pos, out var entered, 16))
        {
            Logger.LogWarning("{Name}: malformed 1605 body ({Len} B), not a Proud WStr", player.Name, payload.Length);
            await SendResultAsync(session, ResultWrongPassword, ct);
            return;
        }

        var store = services.GetRequiredService<IPresentPasswordRepository>();
        bool ok;
        try
        {
            // E-H12: per-account throttle, shared with the gift purchase (1598); a locked-out account is refused
            // without a verification being run.
            ok = await GiftPasswordGate.Shared.CheckAsync(store, player.AccountId, entered, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Present password check failed for account {Account}", player.AccountId);
            ok = false;
        }

        // The password itself is not logged: it is the player's secret.
        Logger.LogInformation("{Name} (account {Account}) checks present password: {Result}",
            player.Name, player.AccountId, ok ? "accepted" : "refused");

        await SendResultAsync(session, ok ? (ushort)0 : ResultWrongPassword, ct);
    }

    private static Task SendResultAsync(ProudSession session, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsCheckPresentPassword, w => w.WriteUInt16(result), ct);
}

/// <summary>
/// E-H12: a per-account brute-force limit on the gift password, which is only a few digits and would otherwise be
/// checkable without bound over 1605 and the gift path of 1598. Every verification of an account
/// that HAS a password spends one unit of <see cref="MaxAttemptsPerAccount"/> per <see cref="WindowMs"/> ms; once
/// spent the check answers "mismatch" without running the verification until the oldest attempt is a full window
/// old. A correct password clears the budget, and an account with no password is never throttled (nothing to guess).
/// The figures mirror SecondPasswordThrottle (S-H4).
/// Process-wide singleton state, shared by <see cref="CheckPresentPasswordHandler"/> and GameMallPurchaseHandler.
/// </summary>
internal sealed class GiftPasswordGate
{
    /// <summary>Verifications admitted per account inside one <see cref="WindowMs"/> window.</summary>
    internal const int MaxAttemptsPerAccount = 10;

    /// <summary>The window length in milliseconds (10 minutes).</summary>
    internal const long WindowMs = 10 * 60 * 1000;

    internal static readonly GiftPasswordGate Shared = new();

    private readonly SlidingWindowRateLimiter<uint> _perAccount = new(MaxAttemptsPerAccount, WindowMs);

    /// <summary>True when <paramref name="entered"/> is accepted: the account has no password, or the password
    /// verifies and the account is not locked out. Store exceptions propagate (the callers treat them as a mismatch).</summary>
    internal async Task<bool> CheckAsync(IPresentPasswordRepository store, uint accountId, string entered, CancellationToken ct)
    {
        if (!await store.HasPasswordAsync(accountId, ct)) return true;
        if (!_perAccount.TryAcquire(accountId)) return false;
        if (!await store.VerifyAsync(accountId, entered, ct)) return false;
        _perAccount.Reset(accountId);
        return true;
    }
}
