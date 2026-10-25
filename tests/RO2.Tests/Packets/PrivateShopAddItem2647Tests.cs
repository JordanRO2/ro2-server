using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 1474 ReqAddPrivateShopItem and its answer 2647 AnsAddPrivateShopItem (PrivateShopAddItemHandler): the two
/// successes and the 23006 refusal sent for all four refusal cases, and the client-side gate the handler mirrors.
/// </summary>
public class PrivateShopAddItem2647Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    // A 1474 (bag 4 slot 12, x1, 2008 zeny) and its 2647, then the same cell again at 1 zeny and its 2647.
    private const string Added2647Index0 = "000000040c7e9b610ea83d040e0100d80700000000000000b05301";
    private const string Added2647Index1 = "000001040c7e9b610ea83d040e0100010000000000000000b05301";

    // A 1474 (bag 1 slot 50, x1, 1 zeny) and the refusal it gets; all four refusal cases carry the same 27 bytes.
    private const string Refused1474 = "013201000100000000000000";
    private const string Refused2647 = "de5900000000000000000000000000000000000000000000000000";
    // A 1474 (bag 1 slot 52, x1, 1 zeny), refused.
    private const string Refused1474Slot52 = "013401000100000000000000";

    [Fact]
    public void Success_IsRetailsFrame()
    {
        Assert.Equal(Convert.FromHexString(Added2647Index0), PrivateShopAddItemHandler.BuildAdded(
            0, 4, 12, 0x0E043DA80E619B7EUL, 1, 2008, 0x0153B000u));
        Assert.Equal(Convert.FromHexString(Added2647Index1), PrivateShopAddItemHandler.BuildAdded(
            1, 4, 12, 0x0E043DA80E619B7EUL, 1, 1, 0x0153B000u));
    }

    [Fact]
    public void Refusal_IsRetailsFrame()
    {
        Assert.Equal(Convert.FromHexString(Refused2647), PrivateShopAddItemHandler.BuildRefused());
        Assert.Equal(23006, PrivateShopAddItemHandler.ResultAddRefused);
    }

    /// <summary>Client-side gate: template +591 (Is_Sell) and +592 (Is_Trade) set and item +576 bit 0x02 (bound)
    /// clear, or the client shows "This item cannot be sold." and never sends the 1474.</summary>
    [Theory]
    [InlineData(1, 1, 0, false, true)]   // sellable, tradeable, never binds
    [InlineData(1, 1, 1, false, true)]   // binds on equip, not yet worn
    [InlineData(1, 1, 1, true, false)]   // binds on equip, worn once (InventoryItem.Bound)
    [InlineData(1, 1, 2, false, false)]  // binds on acquisition
    [InlineData(0, 1, 0, false, false)]  // Is_Sell 0
    [InlineData(1, 0, 0, false, false)]  // Is_Trade 0
    [InlineData(0, 0, 0, false, false)]  // e.g. 40001296
    public void ClientGate_FollowsIsSellIsTradeAndBind(int isSell, int isTrade, int bindType, bool everWorn, bool allowed)
    {
        var template = new ItemData { ID = 1, Is_Sell = (byte)isSell, Is_Trade = (byte)isTrade, Bind_Type = (byte)bindType };
        var item = new InventoryItem { Id = 1, ItemId = 1, StorageType = 0, Bound = everWorn };
        Assert.Equal(allowed, PrivateShopAddItemHandler.ClientGateAllows(template, item));
    }

    [Fact]
    public void ClientGate_RefusesAnItemWithNoTemplate()
        => Assert.False(PrivateShopAddItemHandler.ClientGateAllows(null, new InventoryItem { Id = 1, ItemId = 1 }));

    /// <summary>
    /// End to end through a real ProudServer: retail's refused requests get retail's 27-byte refusal, not
    /// silence. The first names a real bag item (card 22310832, bag 1 slot 50) whose
    /// template this store does not know, the second an empty cell; nothing is listed.
    /// </summary>
    [Fact]
    public async Task RefusedRequests_AreAnsweredWithRetailsRefusal()
    {
        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, server.SessionCount);
        var session = server.GetSessions().Single();
        session.Crypto.SetRc4Key(Rc4Key);
        session.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new PlayerStateManager(new NoPersistence());
        var seller = new PlayerState { SessionId = session.SessionId, Name = "Seller", CharacterId = 7, EntityId = 0x0EBF5511u };
        players.AddPlayer(seller.SessionId, seller);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(server);

        var bag = new FakeInventory(new InventoryItem
        {
            Id = 0x0E043DA80E61AAAAUL, CharacterId = 7, ItemId = 22310832, Count = 1, StorageType = 0, Slot = 0,
        });
        var services = new ServiceCollection()
            .AddSingleton<IInventoryRepository>(bag)
            .AddSingleton(new GameDataStore())
            .BuildServiceProvider();
        var handler = new PrivateShopAddItemHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrivateShopAddItemHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        ushort counter = 1;
        foreach (var request in new[] { Refused1474, Refused1474Slot52 })
        {
            await client.SendAsync(Encrypted(Request(counter++, handler.Opcode, Convert.FromHexString(request))), SocketFlags.None);
            var (op, body) = await ReadInternalRmiAsync(client);
            Assert.Equal(2647, op);
            Assert.Equal(Convert.FromHexString(Refused2647), body);
        }
        Assert.False(PrivateShopRegistry.TryGet(seller, out _, out var listings) && listings.Count > 0);
    }

    // ------------------------------------------------------------------------------------------
    // Transport helpers, as in PartyKick2353Tests.

    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    private static byte[] Request(ushort counter, ushort opcode, byte[] body)
    {
        var plain = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, counter);
        plain[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(3), opcode);
        body.CopyTo(plain, 5);
        return plain;
    }

    private static byte[] Encrypted(byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        var cipher = crypto.Rc4Decrypt(plaintext)!; // symmetric: re-keyed per message
        var header = cipher.Length < 128
            ? new byte[] { 0x25, 0x02, 0x01, (byte)cipher.Length }
            : new byte[] { 0x25, 0x02, 0x02, (byte)cipher.Length, (byte)(cipher.Length >> 8) };
        return ProudFrameCodec.EncodeFrame([.. header, .. cipher]);
    }

    private static async Task<(ushort RmiId, byte[] Body)> ReadInternalRmiAsync(Socket client)
    {
        var head = await ReadExactAsync(client, 3);
        Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
        var lenBytes = await ReadExactAsync(client, head[2]);
        int len = head[2] switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);

        static async Task<byte[]> ReadExactAsync(Socket s, int n)
        {
            var buf = new byte[n];
            for (int got = 0; got < n;)
            {
                int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }
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
