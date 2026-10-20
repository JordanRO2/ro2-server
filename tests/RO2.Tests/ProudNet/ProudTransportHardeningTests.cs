using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.ProudNet;
using Xunit;

namespace RO2.Tests.ProudNet;

/// <summary>
/// Hardening of the ProudNet transport: the frame length bound, the idle and unmatured-handshake timeouts,
/// and the connection caps. Timeouts run off an injected clock and an explicit sweep call, never a wall-clock sleep.
/// </summary>
public sealed class ProudTransportHardeningTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    
 /// <summary>Header of a frame whose length scalar is 4 bytes wide: [13 57][04][i32 LE length].</summary>
    private static byte[] HeaderWithInt32Length(int length)
    {
        var h = new byte[] { 0x13, 0x57, 0x04, 0, 0, 0, 0 };
        BitConverter.TryWriteBytes(h.AsSpan(3), length);
        return h;
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(0x7FFFFFFD)]
    [InlineData(0x7FFFFFFA)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(ProudFrameCodec.MaxFrameBytes + 1)]
    public void HostileDeclaredLength_ThrowsInvalidData_WithoutAllocating(int declared)
    {
        
        var buffer = new ReadOnlySequence<byte>(HeaderWithInt32Length(declared));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Record.Exception(() => ProudFrameCodec.TryParseFrame(ref buffer, out _));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsType<InvalidDataException>(ex); 
        Assert.True(allocated < 32 * 1024, $"rejection allocated {allocated} bytes");
    }

    [Fact]
    public void NegativeInt16Length_ThrowsInvalidData()
    {
        
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0x13, 0x57, 0x02, 0x00, 0x80 });
        Assert.IsType<InvalidDataException>(Record.Exception(() => ProudFrameCodec.TryParseFrame(ref buffer, out _)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(1315)] 
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(ProudFrameCodec.MaxFrameBytes)] 
    public void LegitimateFrames_StillParseByteIdentically(int size)
    {
        var body = new byte[size];
        new Random(size).NextBytes(body);
        var wire = ProudFrameCodec.EncodeFrame(body);
        var buffer = new ReadOnlySequence<byte>(wire);

        Assert.True(ProudFrameCodec.TryParseFrame(ref buffer, out var frame));
        Assert.Equal(body, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void PartialLegitimateFrame_WaitsForMoreData()
    {
        var wire = ProudFrameCodec.EncodeFrame(new byte[1315]);
        var buffer = new ReadOnlySequence<byte>(wire.AsMemory(0, wire.Length - 1));
        Assert.False(ProudFrameCodec.TryParseFrame(ref buffer, out _));
        Assert.Equal(wire.Length - 1, buffer.Length); 
    }

    [Fact]
    public async Task MaximumSizeFrame_ParsesThroughARealSession()
    {
        
        
        await using var rig = await Rig.StartAsync();
        var core = new TaskCompletionSource<(ProudCoreOpCode, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Server.CoreMessageReceived += (_, op, payload) =>
        {
            core.TrySetResult((op, payload.Length));
            return Task.CompletedTask;
        };
        var client = await rig.ConnectAsync();

        var body = new byte[ProudFrameCodec.MaxFrameBytes];
        body[0] = 0x1B;
        await client.SendAsync(ProudFrameCodec.EncodeFrame(body), SocketFlags.None);

        var (op, len) = await core.Task.WaitAsync(Generous);
        Assert.Equal(ProudCoreOpCode.UnreliablePing, op);
        Assert.Equal(ProudFrameCodec.MaxFrameBytes - 1, len);
    }

    [Fact]
    public async Task HostileHeader_ClosesTheSession_InsteadOfParkingThePipe()
    {
        await using var rig = await Rig.StartAsync();
        var client = await rig.ConnectAsync();

        
        await client.SendAsync(HeaderWithInt32Length(0x40000000), SocketFlags.None);

        await rig.Disconnected.Task.WaitAsync(Generous);
        Assert.True(await Rig.IsClosedByPeerAsync(client));
        Assert.Equal(0, rig.Server.SessionCount);
    }

    
    [Fact]
    public async Task SilentSocket_IsClosedAfterTheHandshakeWindow()
    {
        var clock = new ManualTimeProvider();
        await using var rig = await Rig.StartAsync(new ProudServerOptions
        {
            TimeProvider = clock,
            HandshakeTimeout = TimeSpan.FromSeconds(30),
            IdleTimeout = TimeSpan.FromSeconds(120),
            SweepInterval = TimeSpan.FromHours(1), 
        });
        var client = await rig.ConnectAsync();

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await rig.Server.SweepIdleSessionsAsync());
        Assert.Equal(1, rig.Server.SessionCount);

        clock.Advance(TimeSpan.FromSeconds(2)); 
        Assert.Equal(1, await rig.Server.SweepIdleSessionsAsync());

        await rig.Disconnected.Task.WaitAsync(Generous);
        Assert.True(await Rig.IsClosedByPeerAsync(client));
        Assert.Equal(0, rig.Server.SessionCount);
    }

    [Fact]
    public async Task MaturedSession_SurvivesTheHandshakeWindow_ButNotTheIdleWindow()
    {
        var clock = new ManualTimeProvider();
        await using var rig = await Rig.StartAsync(new ProudServerOptions
        {
            TimeProvider = clock,
            HandshakeTimeout = TimeSpan.FromSeconds(30),
            IdleTimeout = TimeSpan.FromSeconds(120),
            SweepInterval = TimeSpan.FromHours(1),
        });
        var client = await rig.ConnectAsync();
        rig.Server.GetSessions().Single().Handshake = HandshakeState.Connected;

        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(0, await rig.Server.SweepIdleSessionsAsync()); 

        
        await client.SendAsync(ProudFrameCodec.EncodeFrame(new byte[] { 0x1B }), SocketFlags.None);
        await WaitUntilAsync(() => rig.Server.GetSessions().Single().LastReceived == clock.GetUtcNow().UtcDateTime);

        clock.Advance(TimeSpan.FromSeconds(119)); 
        Assert.Equal(0, await rig.Server.SweepIdleSessionsAsync());

        clock.Advance(TimeSpan.FromSeconds(2)); 
        Assert.Equal(1, await rig.Server.SweepIdleSessionsAsync());
        await rig.Disconnected.Task.WaitAsync(Generous);
        Assert.True(await Rig.IsClosedByPeerAsync(client));
    }

    
    [Fact]
    public async Task PerIpCap_RefusesTheNextConnection_AndFreesTheSlotOnDisconnect()
    {
        await using var rig = await Rig.StartAsync(new ProudServerOptions { MaxSessionsPerIp = 3, MaxSessions = 100 });
        var accepted = new List<Socket>();
        for (int i = 0; i < 3; i++)
            accepted.Add(await rig.ConnectAsync());
        Assert.Equal(3, rig.Server.SessionCount);

        var fourth = await rig.ConnectAsync(waitForSession: false);
        Assert.True(await Rig.IsClosedByPeerAsync(fourth));
        Assert.Equal(3, rig.Server.SessionCount);
        Assert.Equal(3, rig.ConnectedEvents);

        
        accepted[0].Dispose();
        await WaitUntilAsync(() => rig.Server.SessionCount == 2);
        var again = await rig.ConnectAsync();
        Assert.Equal(3, rig.Server.SessionCount);
        again.Dispose();
    }

    [Fact]
    public async Task PerIpCap_DoesNotStarveAnotherAddress()
    {
        await using var rig = await Rig.StartAsync(new ProudServerOptions { MaxSessionsPerIp = 2, MaxSessions = 100 });
        for (int i = 0; i < 2; i++)
            await rig.ConnectAsync();
        var refused = await rig.ConnectAsync(waitForSession: false);
        Assert.True(await Rig.IsClosedByPeerAsync(refused));

        
        var other = await rig.ConnectAsync(from: IPAddress.Parse("127.0.0.2"));
        Assert.Equal(3, rig.Server.SessionCount);
        other.Dispose();
    }

    [Fact]
    public async Task GlobalCap_RefusesTheConnectionAfterTheCapth()
    {
        await using var rig = await Rig.StartAsync(new ProudServerOptions { MaxSessions = 3, MaxSessionsPerIp = 100 });
        for (int i = 0; i < 3; i++)
            await rig.ConnectAsync();
        Assert.Equal(3, rig.Server.SessionCount);

        var over = await rig.ConnectAsync(waitForSession: false);
        Assert.True(await Rig.IsClosedByPeerAsync(over));
        Assert.Equal(3, rig.Server.SessionCount);
        Assert.Equal(3, rig.ConnectedEvents);
    }

    
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Yield();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public ProudServer Server { get; }
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectedEvents => Volatile.Read(ref _connected);
        private int _connected;
        private readonly List<Socket> _clients = new();

        private Rig(ProudServer server) => Server = server;

        public static async Task<Rig> StartAsync(ProudServerOptions? options = null)
        {
            var server = new ProudServer("Hardening", NullLogger.Instance, options);
            var rig = new Rig(server);
            server.SessionConnected += _ => { Interlocked.Increment(ref rig._connected); return Task.CompletedTask; };
            server.SessionDisconnected += _ => { rig.Disconnected.TrySetResult(); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            return rig;
        }

 /// <summary>Connects a client; by default waits until the server holds one more session.</summary>
        public async Task<Socket> ConnectAsync(bool waitForSession = true, IPAddress? from = null)
        {
            var before = Server.SessionCount;
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (from != null)
                client.Bind(new IPEndPoint(from, 0));
            await client.ConnectAsync(Server.LocalEndPoint!);
            _clients.Add(client);
            if (waitForSession)
                await WaitUntilAsync(() => Server.SessionCount > before);
            return client;
        }

 /// <summary>True once the server has closed this socket. Never true for a live, silent one.</summary>
        public static async Task<bool> IsClosedByPeerAsync(Socket client)
        {
            try
            {
                return await client.ReceiveAsync(new byte[16], SocketFlags.None).WaitAsync(Generous) == 0;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _clients)
                c.Dispose();
            await Server.DisposeAsync();
        }
    }
}
