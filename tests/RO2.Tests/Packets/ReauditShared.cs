using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>Helpers shared by the packet test classes. Import them with <c>using static RO2.Tests.Packets.ReauditShared;</c>, which also brings in the nested types.</summary>
internal static class ReauditShared
{
    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    public static byte[] Hex(string h) => Convert.FromHexString(h);

    public static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

 /// <summary>The repo's data/ folder, found by walking up from the test binaries.</summary>
    public static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "npc", "NpcRetailVitals.json")) && File.Exists(Path.Combine(candidate, "Config_Def.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/npc/NpcRetailVitals.json");
    }

 /// <summary>A GameDataStore holding only the named shipped tables (copied to a temp folder, as the Config_Def test
 /// does), so a test loads what it needs and nothing else.</summary>
    public static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit-a0b2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new GameDataStore();
        try
        {
            foreach (var t in tables) File.Copy(Path.Combine(source, t), Path.Combine(dir, t));
            await store.LoadAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        return store;
    }

    internal sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// <summary>An empty bag with nothing worn.</summary>
    internal sealed class NoGearInventory : IInventoryRepository
    {
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult<InventoryItem?>(null);
        public Task AddItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task RemoveItemAsync(ulong itemId) => Task.CompletedTask;
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

 /// <summary>The worn gear and bag a death-wear test reads and writes.</summary>
    internal sealed class GearInventory(InventoryItem[] items) : IInventoryRepository
    {
        public List<InventoryItem> Items { get; } = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId && i.StorageType == 1).ToList());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { Items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(Items.Count == 0 ? 1UL : Items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

 /// <summary>A loopback ProudServer with N connected clients, each socket known to be its session's.</summary>
    internal sealed class Harness : IAsyncDisposable
    {
        public required ProudServer Server { get; init; }
        public required List<Socket> Clients { get; init; }
        public required List<ProudSession> Sessions { get; init; }
        public required List<FrameReader> Readers { get; init; }

        public static async Task<Harness> StartAsync(int count)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var next = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { Volatile.Read(ref next).TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var clients = new List<Socket>();
            var sessions = new List<ProudSession>();
            for (var i = 0; i < count; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(server.LocalEndPoint!);
                sessions.Add(await next.Task.WaitAsync(Generous));
                clients.Add(client);
                Volatile.Write(ref next, new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            return new Harness { Server = server, Clients = clients, Sessions = sessions,
                Readers = clients.Select(c => new FrameReader(c)).ToList() };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in Clients) c.Dispose();
            await Server.StopAsync();
        }
    }

 /// <summary>Reads the internal RMI frames (<c>[13 57][w][len][01][u16 op][body]</c>) a test client receives.</summary>
    internal sealed class FrameReader(Socket socket)
    {
        public async Task<(ushort Op, byte[] Body)> NextAsync()
        {
            while (true)
            {
                var head = await ReadExactAsync(3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }
    }
}
