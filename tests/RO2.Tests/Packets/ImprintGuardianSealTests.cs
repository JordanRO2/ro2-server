using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>RMI 1694: the seal applies at the request. Happy path stores one line, charges Need_Gold and consumes one
/// seal item; an unconfigured group or a short wallet refuses with result 1 and takes nothing.</summary>
public class ImprintGuardianSealTests
{
    private const uint Accessory = 13000001;   // Equip_Type 24, present in SealSlot_Info
    private const uint SealItem = 22402012;    // Seal_Reinforce_ID 100001, Need_Gold 100000
    private const uint Group = 100001;

    private sealed class Probe(PlayerStateManager pm)
        : ImprintGuardianSealHandler(null!, NullLogger<ImprintGuardianSealHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] b, PlayerState p, IServiceProvider sp) => HandleAsync(s, b, p, sp, CancellationToken.None);
    }

    private class GoldRepo : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) => Task.CompletedTask;
    }

    private static async Task<(ushort result, uint value, InventoryItem target, InventoryItem source, PlayerState player)> RunAsync(
        long gold, bool configured)
    {
        var store = await LoadStoreAsync("ItemInfo.json", "SealItem_Info.json", "SealSlot_Info.json");
        var target = new InventoryItem { Id = 77, CharacterId = 5, ItemId = Accessory, StorageType = 0, Slot = 3 };
        var source = new InventoryItem { Id = 78, CharacterId = 5, ItemId = SealItem, Count = 2, StorageType = 0, Slot = 4 };
        var cfg = new ServerConfiguration();
        cfg.Game.Seal.ReinforceRolls.Clear();   // the test controls the roll table, not the shipped defaults
        if (configured)
            cfg.Game.Seal.ReinforceRolls[Group] = [new SealRollLine { Kind = 1, TraitType = 25, MinValue = 7, MaxValue = 7 }];
        var services = new ServiceCollection()
            .AddSingleton<IInventoryRepository>(new GearInventory([target, source]))
            .AddSingleton(store).AddSingleton(cfg)
            .AddSingleton(DispatchProxy.Create<ICharacterRepository, GoldRepo>())
            .BuildServiceProvider();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptSocketAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var session = new ProudSession(await accept, 0xA7001, NullLogger.Instance);
        try
        {
            var pm = new PlayerStateManager(new NoDirtySink());
            var player = new PlayerState { SessionId = session.SessionId, EntityId = PlayerStateManager.BuildActorHandle(session.SessionId),
                CharacterId = 5, Gold = gold };
            pm.AddPlayer(session.SessionId, player);

            var body = new byte[20];
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), target.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(12), source.Id);
            await new Probe(pm).RunAsync(session, body, player, services);

            for (int i = 0; i < 6; i++)
            {
                var (op, b) = await ReadRmiFrameAsync(client);
                if (op != ZoneS2COpcodes.AnsImprintGuardianSeal) continue;
                Assert.Equal(9, b.Length);
                return (BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(5)), target, source, player);
            }
            throw new Xunit.Sdk.XunitException("no 3031 frame");
        }
        finally
        {
            client.Dispose(); await session.DisposeAsync(); listener.Stop();
        }
    }

    [Fact]
    public async Task Configured_Imprints_ChargesAndConsumes()
    {
        var (result, value, target, source, player) = await RunAsync(150_000, configured: true);
        Assert.Equal((ushort)0, result);
        Assert.Equal(77u, value);
        Assert.Equal([new SealLine(1, 25, 7)], SealLines.Parse(target.SealJson));
        Assert.Equal(50_000, player.Gold);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public async Task UnconfiguredGroup_RefusesAndTakesNothing()
    {
        var (result, _, target, source, player) = await RunAsync(150_000, configured: false);
        Assert.NotEqual((ushort)0, result);
        Assert.Null(target.SealJson);
        Assert.Equal(150_000, player.Gold);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public async Task ShortWallet_RefusesAndTakesNothing()
    {
        var (result, _, target, source, player) = await RunAsync(99_999, configured: true);
        Assert.NotEqual((ushort)0, result);
        Assert.Null(target.SealJson);
        Assert.Equal(99_999, player.Gold);
        Assert.Equal(2, source.Count);
    }

    private static async Task<(ushort op, byte[] body)> ReadRmiFrameAsync(Socket client)
    {
        using var cts = new CancellationTokenSource(Generous);
        var head = await ReadExactAsync(client, 3, cts.Token);
        var lenBytes = await ReadExactAsync(client, head[2], cts.Token);
        int len = head[2] switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len, cts.Token);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
    }

    private static async Task<byte[]> ReadExactAsync(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        for (int got = 0; got < n;)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            Assert.True(r > 0, "connection closed");
            got += r;
        }
        return buf;
    }
}
