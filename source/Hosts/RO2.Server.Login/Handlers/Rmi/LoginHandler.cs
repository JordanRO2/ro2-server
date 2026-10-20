using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.Entities;
using RO2.Domain.Enums;
using RO2.Identity.Application.Commands;
using RO2.Application.Interfaces;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12002 - Login Credentials
/// Client sends username and password for authentication
/// </summary>
public class LoginHandler : LoginRmiHandlerBase<LoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.Login;
    public override string Name => "Login";

    /// <summary>How long a read of <see cref="ConfigPath"/> is reused before the file is looked at again .</summary>
    private const long ConfigCacheTtlMs = 10_000;

    private readonly TimedConfigCache<bool> _allowDuplicateLogin;

    public LoginHandler(IServiceScopeFactory scopeFactory, ILogger<LoginHandler> logger)
        : base(scopeFactory, logger)
    {
        _allowDuplicateLogin = new TimedConfigCache<bool>(ReadAllowDuplicateLogin, () => Environment.TickCount64, ConfigCacheTtlMs);
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {SessionId}: RMI 12002 (Login) received ({Size} bytes)",
            session.SessionId, payload.Length);

        // one authentication per connection. A second 12002 on an authenticated connection would swap
        // the account under it (and the PIN state tied to the first); it is refused before anything is read.
        if (session.AccountId is uint alreadyAuthed)
        {
            Logger.LogWarning("Session {SessionId}: 12002 on a connection already authenticated as account {AccountId}; refused",
                session.SessionId, alreadyAuthed);
            return;
        }

        // 12001 must have matched the b303 client first (VersionCheckHandler). The retail client always
        // sends it before 12002, so a connection that skipped it or failed it is not a client of this build.
        if (!LoginSessionState.For(session).VersionOk)
        {
            Logger.LogWarning("Session {SessionId}: 12002 without a passed 12001 version check; refused", session.SessionId);
            return;
        }

        // RMI 12002 body = fixed 209-byte struct (admin/admin, loginHost "172.22.0.1" as an example):
        //   [0..73]    username  : wchar_t[37] (74B) UTF-16LE, name + null + leaked stack padding
        //   [74..175]  password  : wchar_t[51] (102B) UTF-16LE, plaintext + null + padding
        //   [176..208] loginHost : 33B trailing (ignored)
        // Reading two length-prefixed strings with a fixed skip between them only aligns for names of one particular
        // length; any other length would read a garbage password, so the fixed-width fields are read instead.
        if (payload.Length < 176)
        {
            Logger.LogWarning("Session {SessionId}: Login payload too short ({Size} bytes, need >= 176)",
                session.SessionId, payload.Length);
            return;
        }

        var reader = new PacketReader(payload.Span);

        var username = reader.ReadWideStringFixed(37); // 74 bytes
        var password = reader.ReadWideStringFixed(51); // 102 bytes (remaining 33B = loginHost, ignored)

        // the name is client text; strip control characters (CR/LF) and cap its length for every log
        // line below. The wire echo (12502) keeps the name as sent.
        var logUser = LoginService.SanitizeForLog(username);

        Logger.LogInformation("Session {SessionId}: Login attempt for user {Username}",
            session.SessionId, logUser);

        // Account auth is owned by the Identity service. Dispatch the AuthenticateCommand: it reproduces the
        // legacy flow (unknown username answers 1003, else BCrypt verify + ban check, last-login
        // stamp on success) against the Identity Mongo account store. The wire reply below is rebuilt from the
        // result so the client-facing bytes are unchanged.
        // the LoginThrottle runs first and refuses (IP / account budget) without a BCrypt
        // hash; a refusal is an ordinary failure below.
        var authenticate = services.GetRequiredService<AuthenticateCommandHandler>();
        var result = await services.GetRequiredService<LoginThrottle>().AuthenticateAsync(
            LoginThrottle.KeyFor(session.RemoteEndPoint?.Address),
            new AuthenticateCommand(username, password),
            authenticate,
            LoginThrottle.NowMs,
            ct);

        if (!result.Succeeded)
        {
            // Every failure carries account 0 and the name echoed, as the 1003 and 1004 replies do.
            var code = FailureCode(result.Failure);
            Logger.LogWarning("Session {SessionId}: Authentication failed for {Username} ({Failure}): answering {Code}",
                session.SessionId, logUser, result.Failure, code);
            await LoginService.SendLoginResultAsync(session, code, 0, username);
            return;
        }

        // An account that holds a Zone connection right now is refused with 1008 (account id and name
        // echoed), and that connection is asked to drop, so the next attempt gets
        // in. See OnlineAccountRegistry. Off when config.json allows several clients
        // on one account (AllowsDuplicateLogin).
        if (!_allowDuplicateLogin.Value && await IsOnlineElsewhereAsync(services, session, result.AccountId, ct))
        {
            Logger.LogWarning("Session {SessionId}: {Username} (AccountId {AccountId}) is already logged in; "
                + "answering 1008 and dropping the other connection (set \"{Key}\": true under \"Login\" in "
                + "{Config} to let several clients share one account)",
                session.SessionId, logUser, result.AccountId, AllowDuplicateLoginKey, Path.GetFullPath(ConfigPath));
            await LoginService.SendLoginResultAsync(session, LoginResponseCodes.AlreadyLoggedIn, result.AccountId, username);
            return;
        }

        // Login successful — set the SAME session state the legacy handler set.
        session.AccountId = result.AccountId;
        session.Username = result.Username;
        session.State = SessionState.Authenticated;

        Logger.LogInformation("Session {SessionId}: Login successful - {Username} (AccountId: {AccountId})",
            session.SessionId, logUser, result.AccountId);

        // AnsSecondPassword (12507) is NOT sent here. 12002 is answered with 12502 only; 12507 is
        // the reply to the client's own C2S 12009 ReqSecondPassword, which it sends right after the 12502.
        // Remember the PIN state from this authentication so SecondPasswordRequestHandler can answer with it - stored
        // BEFORE the 12502 goes out, because the 12502 is what makes the client send its 12009.
        SecondPasswordState.AddOrUpdate(session, new StrongBox<bool>(result.HasSecondPassword));

        await LoginService.SendLoginResultAsync(session, LoginResponseCodes.Success, result.AccountId, username);
    }

    /// <summary>
    /// The 12502 code for a failed authentication. The client maps each to its own state and string:
    /// <list type="bullet">
    /// <item>wrong password: 1004, string 2008 "Password is not correct.";</item>
    /// <item>active ban: 1005, string 8949 "This account cannot be logged
    /// in. Please use another account or contact the support team.";</item>
    /// <item>no such account (a name or password an account cannot be registered under): 1003, string
    /// 2007 "Account not registered."</item>
    /// </list>
    /// </summary>
    public static ushort FailureCode(AuthenticateFailure failure) => failure switch
    {
        AuthenticateFailure.Banned => LoginResponseCodes.AccountBlocked,
        AuthenticateFailure.UnknownAccount => LoginResponseCodes.AccountNotFound,
        _ => LoginResponseCodes.WrongPassword,
    };

    /// <summary>The file the Login process loads its ServerConfiguration from (Program.LoadConfiguration).</summary>
    private const string ConfigPath = "config.json";

    /// <summary>Property of the <c>"Login"</c> object in <see cref="ConfigPath"/> that turns the 1008 check off.</summary>
    private const string AllowDuplicateLoginKey = "AllowDuplicateLogin";

    /// <summary>
    /// Whether <see cref="ConfigPath"/> has <c>"Login": { "AllowDuplicateLogin": true }</c>, which lets
    /// several clients be in the world on one account at once: no 1008, and the connection already in is
    /// not dropped. Off (retail) when the property is absent or anything but <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Retail refuses a second login (see OnlineAccountRegistry), but this server can allow two clients on one
    /// account in the world together, which is how party, trade and PvP get tested with one account. Read from the file at most once per <see cref="ConfigCacheTtlMs"/> (it was
    /// a synchronous read on every 12002), so flipping it needs no restart, only that wait. ServerConfiguration
    /// ignores the extra property: the Login, Zone and Chat loaders
    /// deserialize with the default options, which skip unknown members. A file that cannot be read or
    /// parsed counts as off.
    /// </remarks>
    private bool ReadAllowDuplicateLogin()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Login", out var login)
                && login.ValueKind == JsonValueKind.Object
                && login.TryGetProperty(AllowDuplicateLoginKey, out var allow)
                && allow.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read {Key} from {Config}; refusing duplicate logins", AllowDuplicateLoginKey, ConfigPath);
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="accountId"/> holds a live Zone connection. When it does, that connection is
    /// asked to drop (the Zone sends it 2305 reason 15310 within about a second) and true is returned. An
    /// entry whose heartbeat has stopped (a Zone that died or restarted) is deleted and does not count.
    /// A registry failure counts as "not online": the check must never lock an account out.
    /// </summary>
    private async Task<bool> IsOnlineElsewhereAsync(IServiceProvider services, ProudSession session, uint accountId,
        CancellationToken ct)
    {
        try
        {
            var registry = services.GetRequiredService<IOnlineAccountRegistry>();
            var entry = await registry.GetAsync(accountId, ct);
            if (entry is null) return false;

            if (!OnlineAccountRules.IsLive(entry, DateTime.UtcNow))
            {
                await registry.ReleaseAsync(accountId, entry.Owner, ct);
                Logger.LogInformation("Session {SessionId}: cleared a stale online entry for account {AccountId} (last seen {SeenAt:O})",
                    session.SessionId, accountId, entry.SeenAt);
                return false;
            }

            await registry.RequestKickAsync(accountId, entry.Owner, ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {SessionId}: online-account check failed for account {AccountId}",
                session.SessionId, accountId);
            return false;
        }
    }

    /// <summary>
    /// The account's PIN state as of this session's successful 12002, tied to the session object so it goes
    /// away with the connection. Written only on a successful authentication.
    /// </summary>
    private static readonly ConditionalWeakTable<ProudSession, StrongBox<bool>> SecondPasswordState = new();

    /// <summary>
    /// Whether this session authenticated through 12002 and, if so, whether its account has a PIN
    /// (the 12507 flag: 0 = no PIN, client shows "create PIN"; 1 = has PIN, client shows "enter PIN").
    /// </summary>
    internal static bool TryGetHasSecondPassword(ProudSession session, out bool hasPin)
    {
        if (SecondPasswordState.TryGetValue(session, out var box))
        {
            hasPin = box.Value;
            return true;
        }
        hasPin = false;
        return false;
    }
}

/// <summary>
/// A value read with <paramref name="read"/> and reused for <paramref name="ttlMs"/> milliseconds of the
/// <paramref name="nowMs"/> clock before it is read again . Two threads that find it stale at once may
/// both read: harmless, the reads are idempotent and the last one wins.
/// </summary>
public sealed class TimedConfigCache<T>(Func<T> read, Func<long> nowMs, long ttlMs)
{
    private sealed record Entry(T Value, long ExpiresAtMs);

    private volatile Entry? _entry;

    /// <summary>The cached value, re-read when it is older than the time-to-live.</summary>
    public T Value
    {
        get
        {
            var now = nowMs();
            var entry = _entry;
            if (entry is null || now >= entry.ExpiresAtMs)
            {
                entry = new Entry(read(), now + ttlMs);
                _entry = entry;
            }
            return entry.Value;
        }
    }
}
