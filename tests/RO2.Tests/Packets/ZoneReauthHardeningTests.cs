using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Zone re-authentication hardening (backlog S-H7): a 1303 or 1421 on a connection that already holds an account
/// (or whose character is in the world) is refused without burning its token, a failed online-account claim
/// leaves the connection unauthenticated and gives the token back, and a connection has at most one hold loop.
/// Real <see cref="ProudSession"/>s over loopback; the Login token store and the online-account registry are fakes.
/// </summary>
public sealed class ZoneReauthHardeningTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------------------------------------ 1303

    [Fact]
    public async Task SecondToken_OnAnAuthenticatedConnection_IsRefused_FirstAccountKept_OneHoldLoop()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        var tokenA = env.IssueToken(accountId: 11);
        var tokenB = env.IssueToken(accountId: 22);

        await env.ZoneAuth.RunAsync(session, Req1303(tokenA), env.Services);

        Assert.Equal(11u, session.AccountId);
        var (op, _) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.ZoneAuthAck, op);
        await WaitUntilAsync(() => ZoneAuthHandler.ActiveHoldLoops == env.LoopBaseline + 1);

        await env.ZoneAuth.RunAsync(session, Req1303(tokenB), env.Services);

        Assert.Equal(11u, session.AccountId);                       // still A
        Assert.Equal(0, client.Available);                           // the second got no ack
        Assert.True(env.Tokens.Issued.ContainsKey(tokenB));          // and its token was not burned
        Assert.Equal(1, env.Registry.Claims);                        // only A was claimed
        Assert.Equal(env.LoopBaseline + 1, ZoneAuthHandler.ActiveHoldLoops); // one hold loop
    }

    [Fact]
    public async Task ReauthAfterLogout_CancelsTheFirstHoldLoop_SoOnlyOneRuns()
    {
        // LogoutHandler clears AccountId and the connection may authenticate again with a new 1303: the loop of
        // the first login must not keep heartbeating the same owner next to the new one.
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        var first = env.IssueToken(accountId: 31);
        var second = env.IssueToken(accountId: 31);

        await env.ZoneAuth.RunAsync(session, Req1303(first), env.Services);
        await ReadRmiFrameAsync(client);
        await WaitUntilAsync(() => ZoneAuthHandler.ActiveHoldLoops == env.LoopBaseline + 1);

        session.AccountId = null; // what LogoutHandler does
        await env.ZoneAuth.RunAsync(session, Req1303(second), env.Services);
        await ReadRmiFrameAsync(client);

        Assert.Equal(2, env.Registry.Claims);
        // The first loop was waiting out its heartbeat delay: the cancellation ends it, leaving exactly one.
        await WaitUntilAsync(() => ZoneAuthHandler.ActiveHoldLoops == env.LoopBaseline + 1);
        await Task.Delay(300);
        Assert.Equal(env.LoopBaseline + 1, ZoneAuthHandler.ActiveHoldLoops);
    }

    [Fact]
    public async Task ReauthWhileACharacterIsInTheWorld_IsRefused_TokenNotBurned()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        env.Players.AddPlayer(session.SessionId, new PlayerState { SessionId = session.SessionId, EntityId = 1 });
        var token = env.IssueToken(accountId: 41);

        await env.ZoneAuth.RunAsync(session, Req1303(token), env.Services);

        Assert.Null(session.AccountId);
        Assert.Equal(0, client.Available);
        Assert.True(env.Tokens.Issued.ContainsKey(token));
        Assert.Equal(0, env.Registry.Claims);
    }

    [Fact]
    public async Task FailedClaim_LeavesTheConnectionUnauthenticated_GivesTheTokenBack_AndARetryWorks()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        var token = env.IssueToken(accountId: 51);
        env.Registry.FailClaim = true;

        await env.ZoneAuth.RunAsync(session, Req1303(token), env.Services);

        Assert.Null(session.AccountId);                          // not authenticated
        Assert.Equal(0, client.Available);                       // no ack
        Assert.Equal(51u, env.Tokens.Issued.GetValueOrDefault(token)); // the token was given back
        Assert.Equal(env.LoopBaseline, ZoneAuthHandler.ActiveHoldLoops);

        env.Registry.FailClaim = false;
        await env.ZoneAuth.RunAsync(session, Req1303(token), env.Services);

        Assert.Equal(51u, session.AccountId);
        var (op, _) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.ZoneAuthAck, op);
        Assert.False(env.Tokens.Issued.ContainsKey(token));      // consumed for good this time
    }

    [Fact]
    public async Task HoldLoop_EndsOnItsOwn_OnceTheRegistryNoLongerHoldsTheAccount()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        await env.ZoneAuth.RunAsync(session, Req1303(env.IssueToken(accountId: 61)), env.Services);
        await ReadRmiFrameAsync(client);
        await WaitUntilAsync(() => ZoneAuthHandler.ActiveHoldLoops == env.LoopBaseline + 1);

        env.Registry.OpenGate(); // the next heartbeat finds no entry: the loop ends

        await WaitUntilAsync(() => ZoneAuthHandler.ActiveHoldLoops == env.LoopBaseline, TimeSpan.FromSeconds(15));
    }

    // ------------------------------------------------------------------------------------------------ 1421

    [Fact]
    public async Task ChannelMoveLogin_OnAnAuthenticatedConnection_IsRefused_TicketStaysValid()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        session.AccountId = 71;
        var token = Guid.NewGuid().ToByteArray();
        env.Tickets.Issue(token, 71, characterUid: 0x1122334455667788, channelHandle: 0x06514041);

        await env.ChannelMove.RunAsync(session, Req1421(token, 71), env.Services);

        Assert.Equal(0, client.Available);
        Assert.NotNull(env.Tickets.Consume(token, 71)); // not consumed by the refused attempt
    }

    [Fact]
    public async Task ChannelMoveLogin_WhileACharacterIsInTheWorld_IsRefused_TicketStaysValid()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        env.Players.AddPlayer(session.SessionId, new PlayerState { SessionId = session.SessionId, EntityId = 1 });
        var token = Guid.NewGuid().ToByteArray();
        env.Tickets.Issue(token, 72, characterUid: 1, channelHandle: 0x06514041);

        await env.ChannelMove.RunAsync(session, Req1421(token, 72), env.Services);

        Assert.Null(session.AccountId);
        Assert.Equal(0, client.Available);
        Assert.NotNull(env.Tickets.Consume(token, 72));
    }

    [Fact]
    public async Task ChannelMoveLogin_OnABrandNewConnection_StillAuthenticatesFromTheTicket()
    {
        await using var env = new Env();
        var (session, client) = await env.ConnectAsync();
        var token = Guid.NewGuid().ToByteArray();
        env.Tickets.Issue(token, 73, characterUid: 1, channelHandle: 0x06514041);

        await env.ChannelMove.RunAsync(session, Req1421(token, 73), env.Services);

        Assert.Equal(73u, session.AccountId);
        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.AnsChannelMoveLogin, op);
        Assert.Equal(0x06514041u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
    }

    [Fact]
    public async Task IsReauthentication_IsTrueForAnAccountOrAPlayerState_FalseForAFreshConnection()
    {
        await using var env = new Env();
        var (fresh, _) = await env.ConnectAsync();
        var (withAccount, _) = await env.ConnectAsync();
        withAccount.AccountId = 1;
        var (withPlayer, _) = await env.ConnectAsync();
        env.Players.AddPlayer(withPlayer.SessionId, new PlayerState { SessionId = withPlayer.SessionId, EntityId = 1 });

        Assert.False(ZoneAuthHandler.IsReauthentication(fresh, env.Players));
        Assert.True(ZoneAuthHandler.IsReauthentication(withAccount, env.Players));
        Assert.True(ZoneAuthHandler.IsReauthentication(withPlayer, env.Players));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>A 1303 body: 10 bytes of header, the 16-byte token GUID, the (untrusted) claimed account id.</summary>
    private static byte[] Req1303(Guid token)
    {
        var b = new byte[30];
        b[0] = 0x01;
        token.ToByteArray().CopyTo(b, 10);
        return b;
    }

    /// <summary>A 1421 body: token(16), account(4), then the client's address and tail (not read).</summary>
    private static byte[] Req1421(byte[] token, uint account)
    {
        var b = new byte[52];
        token.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), account);
        return b;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Generous);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
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

    private sealed class ProbeZoneAuth() : ZoneAuthHandler(null!, NullLogger<ZoneAuthHandler>.Instance)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
    }

    private sealed class ProbeChannelMove() : ChannelMoveLoginHandler(null!, NullLogger<ChannelMoveLoginHandler>.Instance)
    {
        public Task RunAsync(ProudSession s, byte[] body, IServiceProvider services)
            => HandleAsync(s, body, services, CancellationToken.None);
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

    /// <summary>An online-account registry whose heartbeat parks on a gate, so a started hold loop stays
    /// running until the test opens it (then the heartbeat answers "no entry" and the loop ends).</summary>
    private sealed class FakeRegistry : IOnlineAccountRegistry
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claims;

        public bool FailClaim { get; set; }
        public int Claims => Volatile.Read(ref _claims);
        public void OpenGate() => _gate.TrySetResult();

        public Task ClaimAsync(uint accountId, string owner, CancellationToken ct = default)
        {
            if (FailClaim) throw new InvalidOperationException("registry unreachable");
            Interlocked.Increment(ref _claims);
            return Task.CompletedTask;
        }

        public async Task<OnlineAccountEntry?> HeartbeatAsync(uint accountId, string owner, CancellationToken ct = default)
        {
            await _gate.Task;
            return null;
        }

        public Task<bool> TransferAsync(uint accountId, string fromOwner, string toOwner, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReleaseAsync(uint accountId, string owner, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReleaseAnyAsync(uint accountId, IReadOnlyCollection<string> owners, CancellationToken ct = default) => Task.CompletedTask;
        public Task<OnlineAccountEntry?> GetAsync(uint accountId, CancellationToken ct = default) => Task.FromResult<OnlineAccountEntry?>(null);
        public Task RequestKickAsync(uint accountId, string owner, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePresence : IChannelPresenceStore
    {
        public Task SetAccountChannelAsync(uint accountId, uint channelHandle, CancellationToken ct = default) => Task.CompletedTask;
        public Task<uint?> GetAccountChannelAsync(uint accountId, CancellationToken ct = default) => Task.FromResult<uint?>(null);
        public Task SetCharacterLocationAsync(uint characterId, uint channelHandle, string location, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<uint, string>> GetLocationsAsync(IReadOnlyCollection<uint> characterIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());
    }

    /// <summary>The handlers, their fake stores, and real loopback sessions. Disposing ends any hold loop the
    /// test started, so the next test sees the same <see cref="ZoneAuthHandler.ActiveHoldLoops"/> baseline.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private static uint _nextId = 0xA7200;

        public FakeTokens Tokens { get; } = new();
        public FakeRegistry Registry { get; } = new();
        public ChannelMoveTicketStore Tickets { get; } = new();
        public PlayerStateManager Players { get; } = new(new ReauditShared.NoDirtySink());
        public ProbeZoneAuth ZoneAuth { get; } = new();
        public ProbeChannelMove ChannelMove { get; } = new();
        public IServiceProvider Services { get; }
        public int LoopBaseline { get; } = ZoneAuthHandler.ActiveHoldLoops;

        public Env()
        {
            _listener.Start();
            Services = new ServiceCollection()
                .AddSingleton<IZoneAuthTokenRepository>(Tokens)
                .AddSingleton<IOnlineAccountRegistry>(Registry)
                .AddSingleton(Tickets)
                .AddSingleton(Players)
                .AddSingleton(new BroadcastService(Players, NullLogger<BroadcastService>.Instance))
                .AddSingleton<IChannelPresenceStore>(new FakePresence())
                .BuildServiceProvider();
        }

        public Guid IssueToken(uint accountId)
        {
            var token = Guid.NewGuid();
            Tokens.Issued[token] = accountId;
            return token;
        }

        public async Task<(ProudSession session, Socket client)> ConnectAsync()
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, Interlocked.Increment(ref _nextId), NullLogger.Instance);
            _clients.Add(client);
            _sessions.Add(session);
            return (session, client);
        }

        public async ValueTask DisposeAsync()
        {
            Registry.OpenGate();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (ZoneAuthHandler.ActiveHoldLoops > LoopBaseline && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            foreach (var s in _sessions) await s.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            _listener.Stop();
        }
    }
}
