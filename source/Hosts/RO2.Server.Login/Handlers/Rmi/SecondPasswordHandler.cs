using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Identity.Application.Commands;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12011 - ReqModifySecondPassword (change PIN). Answers 12508 AnsInputSecondPassword.
/// </summary>
/// <remarks>
/// <para>
/// Body: 8 bytes = two u32 PIN words, OLD then NEW, each byte 0x80 | digit (the client packs two 4-character
/// entries into two dwords; a word is 0 when its entry is not exactly four characters).
/// </para>
/// <para>
/// Reply: 12508 AnsInputSecondPassword = 1 as the very next frame ("Completed 2nd password change").
/// 12507 AnsSecondPassword = 1 is the "account has a PIN" flag of the 12009 exchange and is not used
/// here. A wrong old PIN is answered 12508 = 3 ("2nd password is not correct"), the value 12010 also uses.
/// </para>
/// <para>
/// A new PIN equal to the old one is answered 12508 = 4 and nothing is stored. The client maps 4
/// to "You input the same pw as the existing one. Please input another one.", next to
/// 0 and 1 (completed), 2 -> proceed, 3 -> "not correct", 10 -> reopen the PIN window and anything else ->
/// a generic "Error Code" message. The client does not refuse an unchanged PIN itself, so the server does.
/// </para>
/// </remarks>
public class SecondPasswordHandler : LoginRmiHandlerBase<SecondPasswordHandler>
{
    /// <summary>12508 value for a completed change: 0100.</summary>
    private const ushort PinChanged = 1;

    /// <summary>12508 value the client maps to "2nd password is not correct" (same as 12010's refusal).</summary>
    private const ushort PinIncorrect = 3;

    /// <summary>
    /// 12508 value the client maps to string 10424 "You input the same pw as the existing one." Sent when the
    /// new PIN equals the old one.
    /// </summary>
    public const ushort PinSameAsCurrent = 4;

    /// <summary>Size of one PIN word in the 12011 body: a u32 holding four bytes, each <c>0x80 | digit</c>.</summary>
    private const int PinWordSize = 4;

    /// <summary>Offset of the OLD PIN word in the 12011 body.</summary>
    private const int OldPinOffset = 0;

    /// <summary>Offset of the NEW PIN word in the 12011 body.</summary>
    private const int NewPinOffset = PinWordSize;

    /// <summary>12011 body size: the OLD word followed by the NEW word.</summary>
    private const int BodySize = 2 * PinWordSize;

    public override ushort Opcode => LoginC2SOpcodes.ReqModifySecondPassword;
    public override string Name => "SecondPassword";

    public SecondPasswordHandler(IServiceScopeFactory scopeFactory, ILogger<SecondPasswordHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: ReqModifySecondPassword (12011) received ({Size} bytes)",
            session.SessionId, payload.Length);

        if (session.AccountId == null)
            return;

        if (payload.Length < BodySize)
        {
            Logger.LogWarning("Session {Id}: ReqModifySecondPassword body is {Size} bytes, expected {Expected}",
                session.SessionId, payload.Length, BodySize);
            return;
        }

        var oldPinBytes = payload.Slice(OldPinOffset, PinWordSize).ToArray();
        var newPinBytes = payload.Slice(NewPinOffset, PinWordSize).ToArray();

        var accountId = session.AccountId.Value;
        var logUser = LoginService.SanitizeForLog(session.Username);

        // A new entry that is not four 0x80 | digit bytes must never be stored: the client's zero word
        // (blank) would become the PIN "0000". Same rule as the malformed-submit guard in 12010
        // (ClientStateRequestHandler.IsWellFormedPin).
        if (!ClientStateRequestHandler.IsWellFormedPin(newPinBytes))
        {
            Logger.LogWarning("Session {Id}: malformed new PIN in 12011 for {User}", session.SessionId, logUser);
            await SendResultAsync(session, PinIncorrect);
            return;
        }

        // The old-PIN check below is a PIN guess like a 12010, so it spends the same per-account budget.
        // Without this the PIN could be brute-forced through 12011 with no limit. Over the budget the request
        // is refused before the BCrypt verification.
        var pinThrottle = services.GetRequiredService<SecondPasswordThrottle>();
        if (!pinThrottle.TryAttempt(accountId))
        {
            ClientStateRequestHandler.WrongPins.RecordWrong(accountId, Environment.TickCount64);
            Logger.LogWarning("Session {Id}: 12011 over the per-account PIN limit for {User}, refused without a check",
                session.SessionId, logUser);
            await SendResultAsync(session, PinIncorrect);
            return;
        }

        // Same decimal-of-each-byte form 12010 verifies against, so a PIN changed here is accepted there.
        var oldPin = PinDigits(oldPinBytes);
        var newPin = PinDigits(newPinBytes);

        // The old PIN must match the one on record. VerifySecondPasswordCommand only reports WrongPin when a
        // PIN exists; on an account that has none it stores the submitted value, which the set below then
        // replaces, so an account without a PIN simply ends up with the new one.
        var verifyPin = services.GetRequiredService<VerifySecondPasswordCommandHandler>();
        var verifyResult = await verifyPin.HandleAsync(
            new VerifySecondPasswordCommand(accountId, oldPin), ct);
        if (verifyResult.WrongPin)
        {
            ClientStateRequestHandler.WrongPins.RecordWrong(accountId, Environment.TickCount64);
            Logger.LogWarning("Session {Id}: wrong old PIN in 12011 for {User}", session.SessionId, logUser);
            await SendResultAsync(session, PinIncorrect);
            return;
        }
        pinThrottle.Reset(accountId);
        ClientStateRequestHandler.WrongPins.Reset(accountId);

        // The old PIN has just been verified against the stored hash, so a new PIN whose digit form equals
        // it is the PIN on record: answer the client's own "same as the existing one" (4) and store
        // nothing. The digit forms are compared, not the store, because VerifySecondPasswordCommand has
        // side effects and must not run twice.
        if (IsSamePin(oldPin, newPin))
        {
            Logger.LogInformation("Session {Id}: 12011 for {User} names the current PIN as the new one; answered {Code}",
                session.SessionId, logUser, PinSameAsCurrent);
            await SendResultAsync(session, PinSameAsCurrent);
            return;
        }

        // Store PIN hash via the Identity service (BCrypt wf 11 inside the Account aggregate). Account auth /
        // PIN state is owned by Identity now — no RO2.Application IAccountRepository path here.
        var setPin = services.GetRequiredService<SetSecondPasswordCommandHandler>();
        var setResult = await setPin.HandleAsync(
            new SetSecondPasswordCommand(accountId, newPin), ct);
        if (setResult.AccountFound)
        {
            Logger.LogInformation("Session {Id}: Second password changed for {User}",
                session.SessionId, logUser);
        }

        await SendResultAsync(session, PinChanged);
    }

    /// <summary>The digit form a PIN word is stored and verified in (12010 uses the same): the decimal of
    /// each byte, concatenated. The client's bytes are 0x80 | digit, so "1111" is "129129129129".</summary>
    public static string PinDigits(ReadOnlySpan<byte> word)
    {
        var sb = new System.Text.StringBuilder(word.Length * 3);
        foreach (var b in word) sb.Append(b);
        return sb.ToString();
    }

    /// <summary>True when the 12011's new PIN is the old one, compared in the stored digit form.</summary>
    public static bool IsSamePin(string oldPinDigits, string newPinDigits)
        => string.Equals(oldPinDigits, newPinDigits, StringComparison.Ordinal);

    private static Task SendResultAsync(ProudSession session, ushort result) =>
        session.SendRmiAsync(LoginS2COpcodes.AnsInputSecondPassword, writer => writer.WriteUInt16(result));
}
