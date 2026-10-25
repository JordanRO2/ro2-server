using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Re-audit unit A10, end to end through a real <see cref="ProudServer"/>:
/// <list type="bullet">
/// <item>1475 ReqDeletePrivateShopItem → 2648: the answer lists the remaining items and carries an empty title.</item>
/// <item>1329 ReqEnforceProbability, 1671 ReqChangeRefineShopName, 1364 ReqSkill, 1366 ReqPCSkillLearn and
/// 1361 ReqSkillTeleport: the stock client has no request builder for any of them. These tests pin the
/// behaviour for a request the stock client never makes: no answer and no state change.</item>
/// </list>
/// </summary>
public class DeadRequestsReauditTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>An opcode no handler owns: the test's own handler answers it with <see cref="SentinelReply"/>.
    /// Packets of one session are dispatched in order and each handler is awaited, so when the first frame the
    /// client reads after a request is the sentinel's reply, the request's handler sent nothing.</summary>
    private const ushort SentinelRequest = 0xFFF0;
    private const ushort SentinelReply = 0xFFF1;

    // ------------------------------------------------------------------ 2648

    /// <summary>
    /// The seller lists three items (bag 1 slots 0x34, 0x36, 0x37, x255 each; 2647 indices 0, 1, 2), then sends
    /// 1475 <c>02</c> and gets a 2648 with indices 0 and 1 and a zero title. Here the seller has also run and
    /// closed an earlier shop titled "Old" in the same zone session, which BeginSetup does not forget: the
    /// setup-time 2648 still carries a zero title.
    /// </summary>
    [Fact]
    public async Task Delete1475_DuringSetup_IsAnsweredWithRetails2648()
    {
        var items = new[]
        {
            Item(0x0E043DA80F5AD461UL, 0x0206CDF4, flatSlot: 2),   // bag 1 slot 0x34
            Item(0x0E043DA81139A239UL, 0x0206CDF4, flatSlot: 4),   // bag 1 slot 0x36
            Item(0x0E043DA81139A23CUL, 0x0206CDF5, flatSlot: 5),   // bag 1 slot 0x37
        };
        await using var rig = await Rig.StartAsync(new ServiceCollection().AddSingleton<IInventoryRepository>(new FakeInventory(items)));
        var handler = new PrivateShopDeleteItemHandler(rig.ScopeFactory, NullLogger<PrivateShopDeleteItemHandler>.Instance, rig.Players, rig.Broadcast);
        rig.Route(handler);

        var oldTitle = new byte[PrivateShopRegistry.NameBytes];
        System.Text.Encoding.Unicode.GetBytes("Old").CopyTo(oldTitle, 0);
        PrivateShopRegistry.SetName(rig.Player, oldTitle);
        PrivateShopRegistry.TakeViewers(rig.Player);          // 1480: the old shop ends
        PrivateShopRegistry.BeginSetup(rig.Player);           // 1473: a new setup window
        foreach (var it in items)
            Assert.True(PrivateShopRegistry.TryAdd(rig.Player, it, 255, 2500000001, out _));

        await rig.SendAsync(handler.Opcode, [0x02]);
        var (op, body) = await rig.ReadAsync();

        Assert.Equal(2648, op);
        Assert.Equal(Convert.FromHexString(PrivateShopDeleteItem2648Tests.Retail2648TwoItems), body);
    }

    // ------------------------------------------------------------------ requests the stock client never sends

    public static TheoryData<ushort, string> DeadRequests => new()
    {
        // The bodies the old handlers parsed (each acted on them) and a well-formed compact ByteArray.
        { 1329, "0000000000000000" + "f4cd0602" + "05" },          // old: [8-byte header][u32 item][u8 level]
        { 1329, "0105" + "0102030405" },
        { 1671, "05" + "1100000000" },
        { 1364, "08000000" + "e9030000" + "11000006" },            // old: [4][u32 skill][u32 target]
        { 1364, "0108" + "0000000000000000" },
        { 1366, "08000000" + "e9030000" },                         // old: [4][u32 skill]
        { 1361, "0c00" + "0000" + "e9030000" + "4c040000" },       // old: [u16][u16][u32 skill][u32 map]
        { 1361, "0104" + "00000000" },
    };

    /// <summary>
    /// No answer, no state change and no service resolved: the handlers run on a scope that records every
    /// service asked of it, so a handler that still reached for game data, a repository or a combat service
    /// (as the old 1364/1366/1361 handlers did before acting) is caught even when it then gives up.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeadRequests))]
    public async Task DeadRequest_IsNotAnswered_AndChangesNothing(ushort opcode, string bodyHex)
    {
        await using var rig = await Rig.StartAsync(new ServiceCollection());
        var scopes = new RecordingScopeFactory();
        IZoneRmiHandler handler = opcode switch
        {
            1329 => new EnforceProbabilityHandler(scopes, NullLogger<EnforceProbabilityHandler>.Instance, rig.Players, rig.Broadcast),
            1671 => new ChangeRefineShopNameHandler(scopes, NullLogger<ChangeRefineShopNameHandler>.Instance, rig.Players, rig.Broadcast),
            1364 => new SkillUseHandler(scopes, NullLogger<SkillUseHandler>.Instance, rig.Players, rig.Broadcast),
            1366 => new SkillLearnHandler(scopes, NullLogger<SkillLearnHandler>.Instance, rig.Players, rig.Broadcast),
            1361 => new SkillTeleportHandler(scopes, NullLogger<SkillTeleportHandler>.Instance, rig.Players, rig.Broadcast),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode)),
        };
        Assert.Equal(opcode, handler.Opcode);
        rig.Route(handler);

        var p = rig.Player;
        p.MapId = 1100; p.X = 12.5f; p.Hp = 500; p.Mp = 300; p.Gold = 1000; p.MapLoaded = true;
        p.LearnedSkills[1001] = 3;

        await rig.SendAsync(opcode, Convert.FromHexString(bodyHex));
        await rig.SendAsync(SentinelRequest, []);
        var (op, _) = await rig.ReadAsync();

        Assert.Equal(SentinelReply, op);
        Assert.Equal(1, scopes.ScopesCreated);         // the handler ran
        Assert.Empty(scopes.Requested);
        Assert.Equal((1100u, 12.5f, 500, 300, 1000L, true), (p.MapId, p.X, p.Hp, p.Mp, p.Gold, p.MapLoaded));
        Assert.Equal(new Dictionary<uint, byte> { [1001] = 3 }, p.LearnedSkills);
        Assert.Empty(p.SkillCooldowns);
    }

    // ------------------------------------------------------------------ harness

    private static InventoryItem Item(ulong uid, uint itemId, short flatSlot)
        => new() { Id = uid, CharacterId = Rig.CharacterId, ItemId = itemId, Count = 255, Slot = flatSlot, StorageType = 0, Durability = 0 };

    private sealed class Rig : IAsyncDisposable
    {
        internal const uint CharacterId = 41010;
        private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 29 + 7)).ToArray();

        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly ServiceProvider _services;
        private readonly List<IZoneRmiHandler> _handlers = [];
        private ushort _counter = 1;

        internal PlayerStateManager Players { get; }
        internal BroadcastService Broadcast { get; }
        internal PlayerState Player { get; }
        internal IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

        private Rig(ProudServer server, Socket client, ProudSession session, ServiceProvider services)
        {
            _server = server;
            _client = client;
            _services = services;
            Players = new PlayerStateManager(new NoPersistence());
            Player = new PlayerState { SessionId = session.SessionId, Name = "A10", CharacterId = CharacterId, EntityId = 0x0EBFA011u };
            Players.AddPlayer(session.SessionId, Player);
            Broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            Broadcast.SetServer(server);
            server.PacketReceived += async (s, id, payload) =>
            {
                if (id == SentinelRequest)
                {
                    await s.SendInternalRmiAsync(SentinelReply, ReadOnlyMemory<byte>.Empty);
                    return;
                }
                foreach (var h in _handlers)
                    if (h.Opcode == id) await h.HandleAsync(s, payload);
            };
        }

        internal static async Task<Rig> StartAsync(IServiceCollection services)
        {
            var server = new ProudServer("A10", NullLogger.Instance);
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var deadline = DateTime.UtcNow + Generous;
            while (server.SessionCount < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Equal(1, server.SessionCount);
            var session = server.GetSessions().Single();
            session.Crypto.SetRc4Key(Rc4Key);
            session.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)
            return new Rig(server, client, session, services.BuildServiceProvider());
        }

        internal void Route(IZoneRmiHandler handler) => _handlers.Add(handler);

        internal async Task SendAsync(ushort opcode, byte[] body)
        {
            var plain = new byte[5 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(plain, _counter++);
            plain[2] = 0x01;
            BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), opcode);
            body.CopyTo(plain, 5);
            using var crypto = new ProudNetCrypto();
            crypto.SetRc4Key(Rc4Key);
            var cipher = crypto.Rc4Decrypt(plain)!; // symmetric: re-keyed per message
            var header = cipher.Length < 128
                ? new byte[] { 0x25, 0x02, 0x01, (byte)cipher.Length }
                : new byte[] { 0x25, 0x02, 0x02, (byte)cipher.Length, (byte)(cipher.Length >> 8) };
            await _client.SendAsync(ProudFrameCodec.EncodeFrame([.. header, .. cipher]), SocketFlags.None);
        }

        /// <summary>The next UNENC RMI frame the server sent: <c>[13 57][len][01][u16 op][body]</c>.</summary>
        internal async Task<(ushort RmiId, byte[] Body)> ReadAsync()
        {
            var head = await ReadExactAsync(3);
            Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
            var lenBytes = await ReadExactAsync(head[2]);
            int len = head[2] switch
            {
                1 => lenBytes[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(lenBytes),
                _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
            };
            var data = await ReadExactAsync(len);
            Assert.Equal(0x01, data[0]);
            return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
        }

        private async Task<byte[]> ReadExactAsync(int n)
        {
            var buf = new byte[n];
            for (int got = 0; got < n;)
            {
                int r = await _client.ReceiveAsync(buf.AsMemory(got), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _server.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>Hands out scopes whose provider resolves nothing and records every type asked of it.</summary>
    private sealed class RecordingScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        internal int ScopesCreated;
        internal readonly List<Type> Requested = [];
        public IServiceScope CreateScope() { ScopesCreated++; return this; }
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) { Requested.Add(serviceType); return null; }
        public void Dispose() { }
    }

    private sealed class FakeInventory(params InventoryItem[] items) : IInventoryRepository
    {
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<InventoryItem>>(items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<InventoryItem>>([]);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) => throw new NotSupportedException();
        public Task RemoveItemAsync(ulong itemId) => throw new NotSupportedException();
        public Task UpdateItemAsync(InventoryItem item) => throw new NotSupportedException();
        public Task<ulong> GetNextIdAsync() => throw new NotSupportedException();
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

    private sealed class NoPersistence : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
