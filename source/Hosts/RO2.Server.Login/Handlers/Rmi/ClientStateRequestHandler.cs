using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Identity.Application.Commands;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12010 - ReqInputSecondPassword (PIN submit). Answers 12508 AnsInputSecondPassword only.
/// </summary>
/// <remarks>
/// <para>
/// The only S2C between the 12010 and the client's next 12004 ReqChannelList is 12508, and the server list
/// (12503) comes right after a C2S 12004. This handler therefore does not push 12503 itself:
/// <see cref="RequestServerListHandler"/> answers the 12004 that follows, so the list is sent exactly once.
/// </para>
/// <para>
/// Body: one u32, byte i = 0x80 | digit i. The client writes 0 instead when the entry is not exactly four
/// characters, so an all-zero body is a blank submit, not a PIN (0000 goes out as 80 80 80 80). The PIN string
/// keeps the existing decimal-of-each-byte form on purpose: 12011 (SecondPasswordHandler) stores new PINs in
/// that same form, so changing it here alone would reject every PIN already on record.
/// </para>
/// </remarks>
public class ClientStateRequestHandler : LoginRmiHandlerBase<ClientStateRequestHandler>
{
    /// <summary>12508 value for a PIN just created on an account that had none: 0000, answered to the first
    /// 12010 after a 12507 = 0. The client shows 10423 "Succeed in making the 2nd password." and
    /// the PIN window again; only the next submit proceeds.</summary>
    private const ushort PinCreated = 0;

    /// <summary>12508 value for an accepted PIN: 0200.</summary>
    private const ushort PinAccepted = 2;

    /// <summary>12508 value the client maps to "2nd password is not correct" and a re-prompt: 0300 on a
    /// wrong or blank attempt from the 1st to the 4th on a connection.</summary>
    private const ushort PinIncorrect = 3;

    /// <summary>12508 value for a submit that arrives too soon after the last one processed: 0900
    /// (see <see cref="RepeatWindowMs"/>). The client has no dedicated case for it and shows its generic
    /// error text 10437 with the code.</summary>
    private const ushort PinTooSoon = 9;

    /// <summary>12508 value for a wrong attempt from the 5th on a connection: 0a00. It locks nothing: a later
    /// correct submit is still accepted with 0200. The client resets the PIN window without a message.</summary>
    private const ushort PinIncorrectRepeated = 10;

    /// <summary>Number of digits in a PIN; the PIN word is one u32, one byte per digit.</summary>
    private const int PinLength = 4;

    /// <summary>High bit the client sets on every PIN digit byte: byte = <c>PinDigitFlag | digit</c>.</summary>
    private const byte PinDigitFlag = 0x80;

    /// <summary>Largest valid PIN digit byte: <c>PinDigitFlag | 9</c>.</summary>
    private const byte PinDigitMax = PinDigitFlag | 9;

    /// <summary>Wrong attempts on one connection that still get <see cref="PinIncorrect"/>.</summary>
    private const int WrongAttemptsBeforeRepeatedCode = 4;

    /// <summary>
    /// A 12010 that arrives less than this long after the last 12010 PROCESSED on the connection is
    /// answered <see cref="PinTooSoon"/> without being checked, and does not restart the window. It exists to
    /// absorb a double click on the submit button. A 12011 in between does not restart it.
    /// </summary>
    internal const long RepeatWindowMs = 3000;

    public override ushort Opcode => LoginC2SOpcodes.ReqInputSecondPassword;
    public override string Name => "ClientStateRequest";

    public ClientStateRequestHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<ClientStateRequestHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    /// <summary>Per-connection PIN attempt state, tied to the session object so it goes away with the
    /// connection. Only the 12010 repeat window is per connection (see
    /// <see cref="RepeatWindowMs"/>); the wrong-attempt count is per account (<see cref="WrongPins"/>).</summary>
    private sealed class PinAttempts
    {
        public long? LastProcessedMs;
    }

    private static readonly ConditionalWeakTable<ProudSession, PinAttempts> Attempts = new();

    /// <summary>
    /// Wrong PIN attempts per ACCOUNT inside a window, so a new connection does not start the count
    /// over. It picks the 12508 value (<see cref="WrongPinCode"/>); the hard bound on guessing is
    /// <see cref="SecondPasswordThrottle"/>.
    /// </summary>
    public static readonly AccountWrongPinCounter WrongPins = new();

    /// <summary>
    /// Whether a 12010 arriving at <paramref name="nowMs"/> is processed (true) or answered 9 (false), for a
    /// connection whose last processed 12010 arrived at <paramref name="lastProcessedMs"/> (null: none yet).
    /// </summary>
    internal static bool IsProcessed(long? lastProcessedMs, long nowMs)
        => lastProcessedMs is not long last || nowMs - last >= RepeatWindowMs;

    /// <summary>The 12508 value for the <paramref name="wrongCount"/>-th wrong attempt of an account
    /// (1-based).</summary>
    internal static ushort WrongPinCode(int wrongCount)
        => wrongCount > WrongAttemptsBeforeRepeatedCode ? PinIncorrectRepeated : PinIncorrect;

    /// <summary>
    /// True when <paramref name="body"/> holds a PIN word the client can have built: at least 4 bytes, the
    /// first four each <c>0x80 | digit</c> (0x80..0x89). A short or empty body, the builder's all-zero blank
    /// word and any other byte are not a PIN and count as a wrong attempt.
    /// </summary>
    public static bool IsWellFormedPin(ReadOnlySpan<byte> body)
    {
        if (body.Length < PinLength) return false;
        for (var i = 0; i < PinLength; i++)
            if (body[i] is < PinDigitFlag or > PinDigitMax) return false;
        return true;
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: ReqInputSecondPassword (12010) received ({Size} bytes)",
            session.SessionId, payload.Length);

        // A PIN submit only means something after 12002; before it there is no account to check, and
        // answering "accepted" to nobody would let the PIN be skipped.
        if (session.AccountId is not uint accountId)
        {
            Logger.LogWarning("Session {Id}: 12010 before a successful login; not answered", session.SessionId);
            return;
        }

        var logUser = LoginService.SanitizeForLog(session.Username);

        var attempts = Attempts.GetOrCreateValue(session);
        bool processed;
        lock (attempts)
        {
            long now = Environment.TickCount64;
            processed = IsProcessed(attempts.LastProcessedMs, now);
            if (processed) attempts.LastProcessedMs = now;
        }

        if (!processed)
        {
            // It is neither checked nor counted: a submit this soon after an accepted one is typically a repeat of the
            // same PIN.
            Logger.LogDebug("Session {Id}: 12010 within {Window} ms of the last one, answering {Code}",
                session.SessionId, RepeatWindowMs, PinTooSoon);
            await SendResultAsync(session, PinTooSoon);
            return;
        }

        // A body that cannot be a PIN (short, empty, the client's blank word, a byte outside 0x80..0x89)
        // never reaches the Identity command: on an account without a PIN yet a blank would be stored as that
        // account's PIN ("0000" from four zero bytes), and a short body would skip the check and be
        // answered "accepted". Such a body is answered like a wrong PIN (0300) and counted as a wrong attempt.
        if (!IsWellFormedPin(payload.Span))
        {
            int malformedCount = WrongPins.RecordWrong(accountId, Environment.TickCount64);
            Logger.LogWarning("Session {Id}: malformed PIN submit ({Size} bytes) for {User} (wrong attempt {N})",
                session.SessionId, payload.Length, logUser, malformedCount);
            await SendResultAsync(session, WrongPinCode(malformedCount));
            return;
        }

        // Account auth / PIN state is owned by the Identity service: dispatch the
        // VerifySecondPasswordCommand, which reproduces the legacy dual behavior verbatim (verify an existing
        // PIN, or create one on first entry) inside the Account aggregate (BCrypt wf 11).
        // Bound PIN guessing per account, surviving reconnects. Over the budget the submit is refused
        // without the BCrypt verification, so the guess is never tested. A correct PIN resets the budget below.
        var pinThrottle = services.GetRequiredService<SecondPasswordThrottle>();
        if (!pinThrottle.TryAttempt(accountId))
        {
            int overLimit = WrongPins.RecordWrong(accountId, Environment.TickCount64);
            Logger.LogWarning("Session {Id}: PIN submits over the per-account limit for {User}, refused without a check",
                session.SessionId, logUser);
            await SendResultAsync(session, WrongPinCode(overLimit));
            return;
        }

        var pin = SecondPasswordHandler.PinDigits(payload.Span[..PinLength]);

        var verifyPin = services.GetRequiredService<VerifySecondPasswordCommandHandler>();
        var pinResult = await verifyPin.HandleAsync(new VerifySecondPasswordCommand(accountId, pin), ct);

        // 3 -> client shows "2nd password is not correct" (string 10425) and re-prompts.
        // Value 1 would show string 10426 "Completed 2nd password change" instead.
        // (0=made,1=changed,2=proceed,3=incorrect.)
        // An account the store no longer has cannot be proven either: it is refused the same way.
        if (pinResult.WrongPin || !pinResult.AccountFound)
        {
            int wrongCount = WrongPins.RecordWrong(accountId, Environment.TickCount64);
            Logger.LogWarning("Session {Id}: Wrong PIN for {User} (wrong attempt {N}, account found: {Found})",
                session.SessionId, logUser, wrongCount, pinResult.AccountFound);
            await SendResultAsync(session, WrongPinCode(wrongCount));
            return;
        }

        // The PIN was created or verified: clear the per-account budgets so this login never counts
        // against a later one, and open the gate 12005 checks (SelectServerHandler).
        pinThrottle.Reset(accountId);
        WrongPins.Reset(accountId);
        LoginSessionState.For(session).PinVerified = true;

        if (!pinResult.HadExistingPin)
        {
            // The command just stored this entry as the account's first PIN. The client is not let in on that
            // submit: the answer is 0 and the client asks for the PIN again.
            Logger.LogInformation("Session {Id}: PIN created for {User}", session.SessionId, logUser);
            await SendResultAsync(session, PinCreated);
            return;
        }

        Logger.LogDebug("Session {Id}: PIN verified for {User}", session.SessionId, logUser);

        // AnsInputSecondPassword (12508) - proceed. The client answers it with 12004, and the server list
        // goes out from RequestServerListHandler in reply to that, exactly once.
        await SendResultAsync(session, PinAccepted);
    }

    private static Task SendResultAsync(ProudSession session, ushort value)
        => session.SendRmiAsync(LoginS2COpcodes.AnsInputSecondPassword, writer => writer.WriteUInt16(value));
}

/// <summary>
/// Wrong PIN attempts per account inside a fixed window. The count starts at the first wrong attempt
/// and restarts once <see cref="WindowMs"/> have passed since then, so a new connection does not reset it.
/// <see cref="RecordWrong"/> is one atomic read-modify-write on the account's slot (the update delegate is
/// pure, so a contended retry is harmless). Time is a monotonic-clock millisecond value so tests are
/// deterministic.
/// </summary>
public sealed class AccountWrongPinCounter
{
    /// <summary>The window length in milliseconds (10 minutes, the same as <see cref="SecondPasswordThrottle"/>).</summary>
    public const long WindowMs = SecondPasswordThrottle.WindowMs;

    /// <summary>Every this many <see cref="RecordWrong"/> calls the table is swept of expired accounts.</summary>
    private const int PruneEveryCalls = 256;

    private sealed record Window(long StartMs, int Count);

    private readonly ConcurrentDictionary<uint, Window> _windows = new();
    private long _calls;

    /// <summary>Accounts currently tracked (diagnostics and tests).</summary>
    public int TrackedAccounts => _windows.Count;

    /// <summary>Counts one wrong attempt of <paramref name="accountId"/> and returns how many it has made in
    /// its current window (1 for the first).</summary>
    public int RecordWrong(uint accountId, long nowMs)
    {
        if (Interlocked.Increment(ref _calls) % PruneEveryCalls == 0)
            Prune(nowMs);

        return _windows.AddOrUpdate(
            accountId,
            static (_, now) => new Window(now, 1),
            static (_, current, now) => now - current.StartMs >= WindowMs ? new Window(now, 1) : current with { Count = current.Count + 1 },
            nowMs).Count;
    }

    /// <summary>Forgets <paramref name="accountId"/> (a correct PIN).</summary>
    public void Reset(uint accountId) => _windows.TryRemove(accountId, out _);

    /// <summary>Drops every account whose window has expired at <paramref name="nowMs"/>. Returns how many.</summary>
    public int Prune(long nowMs)
    {
        var removed = 0;
        foreach (var pair in _windows)
            if (nowMs - pair.Value.StartMs >= WindowMs && _windows.TryRemove(pair))
                removed++;
        return removed;
    }
}
