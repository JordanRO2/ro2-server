using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Application.Commands;
using RO2.Identity.Domain.Account;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Configuration;
using RO2.Server.Login.Handlers.Rmi;
using RO2.Server.Login.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Login/session hardening (backlog S-H4, S-H8, S-H10, S-H11, S-H13/H14): the PIN gate in front of 12005, the
/// wrong-PIN count per account, the 12010/12011 guess budget, 12005 handle validation, the 12001 gate on 12002,
/// pre-auth answers, log sanitization and the cached config read. Real <see cref="ProudSession"/>s over
/// loopback, handlers run through their own <c>HandleAsync</c> with fake stores.
/// </summary>
public sealed class LoginSessionHardeningTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>Channel 1 of server index 1: the handle 12503 advertises first (0x06514041).</summary>
    private const uint Channel1Handle = 0x06514041u;

    // ---------------------------------------------------------------------------------------- S-H4 + S-H8: 12005

    [Fact]
    public async Task SelectServer_WithoutVerifiedPin_IssuesNoTokenAndSendsNoAck()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = 7101;
        var h = new Harness();

        await h.SelectServer.RunAsync(session, Handle(Channel1Handle), h.Services);

        Assert.Empty(h.Tokens.Issued);
        Assert.Empty(h.Presence.Set);
        Assert.Equal(0, client.Available);
    }

    [Fact]
    public async Task SelectServer_AfterPinVerified_IssuesOneTokenForTheAccount_AndAcks()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = 7102;
        LoginSessionState.For(session).PinVerified = true;
        var h = new Harness();

        await h.SelectServer.RunAsync(session, Handle(Channel1Handle), h.Services);

        var account = Assert.Single(h.Tokens.Issued.Values);
        Assert.Equal(7102u, account);
        Assert.Equal((7102u, Channel1Handle), Assert.Single(h.Presence.Set));
        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(LoginS2COpcodes.SelectServerAck, op);
        Assert.Equal(Channel1Handle, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + 2)));
    }

    [Fact]
    public async Task SelectServer_UnauthenticatedConnection_IssuesNothingAndSendsNothing()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        LoginSessionState.For(session).PinVerified = true; // even with the flag, no account means no token
        var h = new Harness();

        await h.SelectServer.RunAsync(session, Handle(Channel1Handle), h.Services);

        Assert.Empty(h.Tokens.Issued);
        Assert.Equal(0, client.Available);
    }

    [Theory]
    [InlineData(0xDEADBEEFu)]
    [InlineData(0u)]
    [InlineData(0x06F14001u)] // a well-formed handle for a channel the list does not have
    public async Task SelectServer_UnadvertisedHandle_GetsNoTokenAndNoPresenceRow(uint handle)
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = 7103;
        LoginSessionState.For(session).PinVerified = true;
        var h = new Harness();

        await h.SelectServer.RunAsync(session, Handle(handle), h.Services);

        Assert.Empty(h.Tokens.Issued);
        Assert.Empty(h.Presence.Set);
        Assert.Equal(0, client.Available);
    }

    [Fact]
    public async Task SelectServer_ShortBody_IsNotAnswered()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = 7104;
        LoginSessionState.For(session).PinVerified = true;
        var h = new Harness();

        await h.SelectServer.RunAsync(session, [0x41, 0x40], h.Services);

        Assert.Empty(h.Tokens.Issued);
        Assert.Equal(0, client.Available);
    }

    // ---------------------------------------------------------------------------------------- S-H4: 12010

    [Fact]
    public async Task EmptyPinBody_IsAWrongAttempt_Reply3_NotAccepted2()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        const uint account = 7201;
        session.AccountId = account;
        var h = new Harness();

        await h.ClientState.RunAsync(session, [], h.Services);

        Assert.Equal(3, await ReadPinReplyAsync(client));
        Assert.False(LoginSessionState.For(session).PinVerified);
        // The next wrong attempt of the account (a new connection) is number 2.
        Assert.Equal(2, ClientStateRequestHandler.WrongPins.RecordWrong(account, Environment.TickCount64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("818181")]                // short
    [InlineData("00000000")]              // the builder's blank word
    [InlineData("81818190")]              // 0x90 is not 0x80 | digit
    [InlineData("7f818181")]              // below 0x80
    [InlineData("31323334")]              // plain ASCII digits, not the client's 0x80 | digit
    public async Task MalformedPinBody_NeverReachesTheVerification(string hex)
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = NextAccount(); // the wrong count is per account: each case gets its own
        var h = new Harness();
        var before = h.Hasher.Verifications;

        await h.ClientState.RunAsync(session, Convert.FromHexString(hex), h.Services);

        Assert.Equal(3, await ReadPinReplyAsync(client));
        Assert.Equal(before, h.Hasher.Verifications);
        Assert.False(LoginSessionState.For(session).PinVerified);
    }

    [Fact]
    public async Task PinWrongCount_FollowsTheAccountAcrossFreshConnections()
    {
        // Attempts 1..4 get 3 and the 5th gets 10, counted per account: every attempt here is on a
        // new connection, which used to start the count over at 1 each time.
        await using var net = new Loopback();
        const uint account = 7203;
        var h = new Harness();
        var replies = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var (session, client) = await net.ConnectAsync();
            session.AccountId = account;
            await h.ClientState.RunAsync(session, [], h.Services);
            replies.Add(await ReadPinReplyAsync(client));
        }

        Assert.Equal(new[] { 3, 3, 3, 3, 10, 10 }, replies);
    }

    [Fact]
    public async Task CorrectPin_IsAccepted_OpensTheGate_AndClearsTheWrongCount()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        const uint account = 7204;
        session.AccountId = account;
        var h = new Harness();
        h.AddAccount(account, pinDigits: "129129129129"); // PIN 1111, stored as the decimal of each byte
        ClientStateRequestHandler.WrongPins.RecordWrong(account, Environment.TickCount64);

        await h.ClientState.RunAsync(session, Convert.FromHexString("81818181"), h.Services);

        Assert.Equal(2, await ReadPinReplyAsync(client));
        Assert.True(LoginSessionState.For(session).PinVerified);
        Assert.Equal(1, ClientStateRequestHandler.WrongPins.RecordWrong(account, Environment.TickCount64)); // was reset
    }

    [Fact]
    public async Task WrongPin_IsRefused3_AndLeavesTheGateClosed()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        const uint account = 7205;
        session.AccountId = account;
        var h = new Harness();
        h.AddAccount(account, pinDigits: "129129129129");

        await h.ClientState.RunAsync(session, Convert.FromHexString("82828282"), h.Services);

        Assert.Equal(3, await ReadPinReplyAsync(client));
        Assert.False(LoginSessionState.For(session).PinVerified);
    }

    [Fact]
    public async Task FirstPinOnAnAccountWithoutOne_AnswersCreated0_AndOpensTheGate()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        const uint account = 7206;
        session.AccountId = account;
        var h = new Harness();
        h.AddAccount(account, pinDigits: null);

        await h.ClientState.RunAsync(session, Convert.FromHexString("81828384"), h.Services);

        Assert.Equal(0, await ReadPinReplyAsync(client));
        Assert.True(LoginSessionState.For(session).PinVerified);
    }

    [Fact]
    public async Task PinSubmitBeforeLogin_IsNotAnswered()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();

        await h.ClientState.RunAsync(session, Convert.FromHexString("81818181"), h.Services);
        await h.ClientState.RunAsync(session, [], h.Services);

        Assert.Equal(0, client.Available);
        Assert.False(LoginSessionState.For(session).PinVerified);
    }

    // ---------------------------------------------------------------------------------------- S-H4: 12011

    [Fact]
    public async Task ChangePin12011_GuessesAcrossFreshSessions_HitTheAccountCap()
    {
        // 11 wrong old PINs, each on a fresh connection: the first MaxAttemptsPerAccount are verified (and
        // refused), the next is refused without a verification.
        await using var net = new Loopback();
        const uint account = 7301;
        var h = new Harness();
        h.AddAccount(account, pinDigits: "129129129129");
        var oldHashes = h.Hasher.Verifications;

        for (var i = 0; i <= SecondPasswordThrottle.MaxAttemptsPerAccount; i++)
        {
            var (session, client) = await net.ConnectAsync();
            session.AccountId = account;
            // old 2222 (wrong), new 3333
            await h.SecondPassword.RunAsync(session, Convert.FromHexString("8282828283838383"), h.Services);
            Assert.Equal(3, await ReadPinReplyAsync(client));
        }

        Assert.Equal(SecondPasswordThrottle.MaxAttemptsPerAccount, h.Hasher.Verifications - oldHashes);
    }

    [Fact]
    public async Task ChangePin12011_MalformedNewPin_IsRefusedWithoutAVerification()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        session.AccountId = 7302;
        var h = new Harness();
        h.AddAccount(7302, pinDigits: "129129129129");
        var before = h.Hasher.Verifications;

        await h.SecondPassword.RunAsync(session, Convert.FromHexString("8181818100000000"), h.Services);

        Assert.Equal(3, await ReadPinReplyAsync(client));
        Assert.Equal(before, h.Hasher.Verifications);
    }

    // ---------------------------------------------------------------------------------------- S-H13: pre-auth answers

    [Fact]
    public async Task ServerListRequest_BeforeLogin_IsNotAnswered_AndAfterLogin_Is()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();

        await h.ServerList.RunAsync(session, [], h.Services);
        Assert.Equal(0, client.Available);

        session.AccountId = 7401;
        await h.ServerList.RunAsync(session, [], h.Services);
        var (op, _) = await ReadRmiFrameAsync(client);
        Assert.Equal(LoginS2COpcodes.ServerGroupList, op);
    }

    // ---------------------------------------------------------------------------------------- S-H7 + S-H10: 12001 / 12002

    [Fact]
    public async Task VersionCheck_MatchingClient_AnswersOk_AndOpensTheGate()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();

        await h.Version.RunAsync(session, B303VersionBody(), h.Services);

        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(LoginS2COpcodes.VersionCheckResult, op);
        Assert.Equal(new byte[10], body);
        Assert.True(LoginSessionState.For(session).VersionOk);
    }

    [Theory]
    [InlineData(0)]   // wrong length
    [InlineData(1)]   // wrong header
    [InlineData(2)]   // wrong build words
    public async Task VersionCheck_Mismatch_AnswersVersionFailed_AndKeepsTheGateClosed(int kind)
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();
        var body = B303VersionBody();
        body = kind switch
        {
            0 => body[..15],
            1 => [.. new byte[] { 0x11 }, .. body[1..]],
            _ => [.. body[..8], .. new byte[] { 0x01, 0x00, 0x00, 0x00, 0x02, 0x07, 0x02, 0x25 }],
        };

        await h.Version.RunAsync(session, body, h.Services);

        var (op, reply) = await ReadRmiFrameAsync(client);
        Assert.Equal(LoginS2COpcodes.VersionCheckResult, op);
        Assert.Equal(10, reply.Length);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(8))); // the client's state-4 path
        Assert.False(LoginSessionState.For(session).VersionOk);
    }

    [Fact]
    public void VersionCheck_OkBodyIsAllZeros_MismatchBodyDiffersOnlyInTheResultWord()
    {
        Assert.Equal(new byte[10], VersionCheckHandler.BuildVersionResult12501(true));
        var failed = VersionCheckHandler.BuildVersionResult12501(false);
        Assert.Equal(new byte[8], failed[..8]);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(failed.AsSpan(8)));
        Assert.True(VersionCheckHandler.IsSupportedClientVersion(B303VersionBody()));
    }

    [Fact]
    public void VersionCheck_PristineClientWithTrailingBuildTag_StillMatches()
    {
        // The b303 client sends 27 bytes: the 16-byte fingerprint it declares (innerSize 16) then a build tag
        // ("bfc1-0…"). It must match on the 16-byte prefix — else a plain login shows the client's "version wrong"
        // screen and freezes.
        var body = Convert.FromHexString("100021cba416f1000100000001070225626663312d30531aa7d906");
        Assert.Equal(27, body.Length);
        Assert.True(VersionCheckHandler.IsSupportedClientVersion(body));
    }

    [Fact]
    public async Task Login12002_WithoutAPassed12001_IsRefusedBeforeAnyWork()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();

        // An empty provider: reaching the Identity command (GetRequiredService) would throw.
        await h.Login.RunAsync(session, LoginBody("tester", "pw"), new ServiceCollection().BuildServiceProvider());

        Assert.Null(session.AccountId);
        Assert.Equal(0, client.Available);
    }

    [Fact]
    public async Task Login12002_OnAnAlreadyAuthenticatedConnection_IsRefused_AccountIsKept()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var h = new Harness();
        LoginSessionState.For(session).VersionOk = true;
        session.AccountId = 7501;

        await h.Login.RunAsync(session, LoginBody("other", "pw"), new ServiceCollection().BuildServiceProvider());

        Assert.Equal(7501u, session.AccountId);
        Assert.Equal(0, client.Available);
    }

    // ---------------------------------------------------------------------------------------- S-H11: log sanitization

    [Fact]
    public void SanitizeForLog_RemovesNewlinesAndControlCharacters_AndCapsTheLength()
    {
        var cleaned = LoginService.SanitizeForLog("admin\r\n[Warning] forged\u2028line\u0085\tend");

        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\u2028', cleaned);
        Assert.DoesNotContain('\u0085', cleaned);
        Assert.DoesNotContain('\t', cleaned);
        Assert.Equal("admin[Warning] forgedlineend", cleaned);
        Assert.Equal(LoginService.MaxLoggedClientStringLength, LoginService.SanitizeForLog(new string('a', 5000)).Length);
        Assert.Equal("", LoginService.SanitizeForLog(null));
    }

    [Fact]
    public async Task Login12002_UsernameWithNewline_NeverReachesALogLineRaw()
    {
        await using var net = new Loopback();
        var (session, _) = await net.ConnectAsync();
        LoginSessionState.For(session).VersionOk = true;
        var log = new CapturingLogger<LoginHandler>();
        var handler = new ProbeLogin(log);

        // The empty provider stops the handler right after its first log line (the Identity command is not
        // there), which is the line that carried the raw username.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            handler.RunAsync(session, LoginBody("evil\nuser\r[Critical] forged", "pw"), new ServiceCollection().BuildServiceProvider()));

        var lines = log.Messages.Where(m => m.Contains("Login attempt", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(lines);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("eviluser[Critical] forged", line, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------- S-H14: cached config

    [Fact]
    public void TimedConfigCache_ReadsOncePerTtl_NotOncePerCall()
    {
        long now = 1_000;
        var reads = 0;
        var cache = new TimedConfigCache<bool>(() => { reads++; return reads % 2 == 1; }, () => now, ttlMs: 10_000);

        for (var i = 0; i < 100; i++) Assert.True(cache.Value);
        Assert.Equal(1, reads);

        now += 9_999;
        Assert.True(cache.Value);
        Assert.Equal(1, reads);

        now += 1; // the TTL has passed: the next read sees the flipped value
        Assert.False(cache.Value);
        Assert.Equal(2, reads);
    }

    // ---------------------------------------------------------------------------------------- per-account wrong count

    [Fact]
    public void AccountWrongPinCounter_CountsPerAccount_InAFixedWindow()
    {
        var counter = new AccountWrongPinCounter();
        Assert.Equal(1, counter.RecordWrong(1, 1_000));
        Assert.Equal(2, counter.RecordWrong(1, 2_000));
        Assert.Equal(1, counter.RecordWrong(2, 2_000)); // another account starts at 1
        Assert.Equal(3, counter.RecordWrong(1, 1_000 + AccountWrongPinCounter.WindowMs - 1));

        // Once a full window has passed since the window's first wrong attempt the count starts over.
        Assert.Equal(1, counter.RecordWrong(1, 1_000 + AccountWrongPinCounter.WindowMs));

        counter.Reset(1);
        Assert.Equal(1, counter.RecordWrong(1, 5_000));
    }

    [Fact]
    public void AccountWrongPinCounter_ConcurrentAttempts_AreAllCounted()
    {
        var counter = new AccountWrongPinCounter();
        Parallel.For(0, 2_000, _ => counter.RecordWrong(9, 100));
        Assert.Equal(2_001, counter.RecordWrong(9, 100));
    }

    [Fact]
    public void AccountWrongPinCounter_ExpiredAccountsArePruned()
    {
        var counter = new AccountWrongPinCounter();
        for (uint a = 1; a <= 50; a++) counter.RecordWrong(a, 0);
        Assert.Equal(50, counter.TrackedAccounts);

        Assert.Equal(50, counter.Prune(AccountWrongPinCounter.WindowMs));
        Assert.Equal(0, counter.TrackedAccounts);
    }

    [Theory]
    [InlineData("81828384", true)]
    [InlineData("89898989", true)]
    [InlineData("80808080", true)]
    [InlineData("81828384ff", true)] // extra bytes beyond the word are not part of the PIN
    [InlineData("8182838a", false)]
    [InlineData("0182838a", false)]
    [InlineData("818283", false)]
    [InlineData("", false)]
    public void IsWellFormedPin_AcceptsOnlyFourBytesOf0x80Or0x89(string hex, bool expected)
        => Assert.Equal(expected, ClientStateRequestHandler.IsWellFormedPin(Convert.FromHexString(hex)));

    // ---------------------------------------------------------------------------------------- helpers

    private static int _nextAccountCounter = 7900;

    private static uint NextAccount() => (uint)Interlocked.Increment(ref _nextAccountCounter);

    private static byte[] Handle(uint handle)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, handle);
        return b;
    }

    /// <summary>The 16 bytes of the b303 client's 12001 (the handler's own reference).</summary>
    private static byte[] B303VersionBody()
        => Convert.FromHexString("100021cba416f100" + "01000000" + "01070225");

    /// <summary>A 12002 body: the 209-byte struct, name wchar[37] at 0, password wchar[51] at 74.</summary>
    private static byte[] LoginBody(string name, string password)
    {
        var b = new byte[209];
        Encoding.Unicode.GetBytes(name).CopyTo(b, 0);
        Encoding.Unicode.GetBytes(password).CopyTo(b, 74);
        return b;
    }

    private static async Task<int> ReadPinReplyAsync(Socket client)
    {
        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(LoginS2COpcodes.AnsInputSecondPassword, op);
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    /// <summary>Reads one ProudNet frame <c>[u16 magic][scalar len][01][u16 rmi][body]</c>.</summary>
    private static async Task<(ushort op, byte[] body)> ReadRmiFrameAsync(Socket client)
    {
        using var cts = new CancellationTokenSource(Generous);
        var head = await ReadExactAsync(client, 3, cts.Token);
        int width = head[2];
        var lenBytes = await ReadExactAsync(client, width, cts.Token);
        int len = width switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len, cts.Token);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
    }

    private static async Task<byte[]> ReadExactAsync(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            Assert.True(r > 0, "connection closed");
            got += r;
        }
        return buf;
    }

    // ---- handler probes (HandleAsync is protected) ----

    private sealed class ProbeSelectServer(ServerConfiguration config)
        : SelectServerHandler(null!, NullLogger<SelectServerHandler>.Instance, config)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeClientState() : ClientStateRequestHandler(null!, NullLogger<ClientStateRequestHandler>.Instance)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeSecondPassword() : SecondPasswordHandler(null!, NullLogger<SecondPasswordHandler>.Instance)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeServerList(ServerConfiguration config)
        : RequestServerListHandler(null!, NullLogger<RequestServerListHandler>.Instance, config)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeVersion() : VersionCheckHandler(null!, NullLogger<VersionCheckHandler>.Instance, new ServerConfiguration())
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeLogin(ILogger<LoginHandler> logger) : LoginHandler(null!, logger)
    {
        public ProbeLogin() : this(NullLogger<LoginHandler>.Instance) { }

        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    /// <summary>The handlers and the fake stores they run against, wired the way the Login host wires them.</summary>
    private sealed class Harness
    {
        public FakeHasher Hasher { get; } = new();
        public FakeTokens Tokens { get; } = new();
        public FakePresence Presence { get; } = new();
        public InMemoryAccounts Accounts { get; } = new();
        public IServiceProvider Services { get; }

        public ProbeSelectServer SelectServer { get; }
        public ProbeClientState ClientState { get; } = new();
        public ProbeSecondPassword SecondPassword { get; } = new();
        public ProbeServerList ServerList { get; }
        public ProbeVersion Version { get; } = new();
        public ProbeLogin Login { get; } = new();

        public Harness()
        {
            var config = new ServerConfiguration();
            SelectServer = new ProbeSelectServer(config);
            ServerList = new ProbeServerList(config);
            Services = new ServiceCollection()
                .AddSingleton<IZoneAuthTokenRepository>(Tokens)
                .AddSingleton<IChannelPresenceStore>(Presence)
                .AddSingleton(new SecondPasswordThrottle())
                .AddSingleton(new VerifySecondPasswordCommandHandler(Accounts, Hasher))
                .BuildServiceProvider();
        }

        public void AddAccount(uint id, string? pinDigits)
        {
            var account = Account.Register(AccountId.From(id), "user" + id, "pw", Hasher, new FixedClock());
            if (pinDigits is not null) account.SetSecondPassword(pinDigits, Hasher);
            Accounts.Store[id] = account;
        }
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        private int _verifications;
        public int Verifications => Volatile.Read(ref _verifications);
        public string Hash(string plaintext) => "H:" + plaintext;

        public bool Verify(string plaintext, string hash)
        {
            Interlocked.Increment(ref _verifications);
            return hash == "H:" + plaintext;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class InMemoryAccounts : IAccountRepository
    {
        public Dictionary<uint, Account> Store { get; } = [];

        public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
            => Task.FromResult(Store.GetValueOrDefault(id.Value));

        public Task AddAsync(Account aggregate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Account aggregate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Account?>(null);
        public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<AccountId> NextIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(AccountId.From(1));
    }

    private sealed class FakeTokens : IZoneAuthTokenRepository
    {
        public Dictionary<Guid, uint> Issued { get; } = [];

        public Task IssueAsync(Guid token, uint accountId, TimeSpan ttl)
        {
            Issued[token] = accountId;
            return Task.CompletedTask;
        }

        public Task<uint?> ConsumeAsync(Guid token)
            => Task.FromResult(Issued.Remove(token, out var account) ? (uint?)account : null);
    }

    private sealed class FakePresence : IChannelPresenceStore
    {
        public List<(uint Account, uint Handle)> Set { get; } = [];

        public Task SetAccountChannelAsync(uint accountId, uint channelHandle, CancellationToken ct = default)
        {
            Set.Add((accountId, channelHandle));
            return Task.CompletedTask;
        }

        public Task<uint?> GetAccountChannelAsync(uint accountId, CancellationToken ct = default) => Task.FromResult<uint?>(null);
        public Task SetCharacterLocationAsync(uint characterId, uint channelHandle, string location, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<uint, string>> GetLocationsAsync(IReadOnlyCollection<uint> characterIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 0xA7100;

        public Loopback() => _listener.Start();

        public async Task<(ProudSession session, Socket client)> ConnectAsync()
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance);
            _clients.Add(client);
            _sessions.Add(session);
            return (session, client);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in _sessions) await s.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            _listener.Stop();
        }
    }
}
