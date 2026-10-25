using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Shared corpse loot (GroundItemService.SharesCorpseLoot). In Roll Dice, Free for All and Wearer Roll Dice parties, each member on the killer's map gets its own 2471 grant before the 2392 frame, may open the corpse and take its items, and loses the grant when the corpse holds nothing.</summary>
[Collection(CorpseShareRigCollection.Name)]
public sealed class SharedCorpseLootTests
{
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private const uint RetailCorpse = 0x019DE813u;
    private const ulong RetailMemberGid = 0x0DE0B6B3A7E7D981UL;

 /// <summary>The member's grant for a corpse its party mate killed, sent before that corpse's 2392 frame.</summary>
    private const string RetailMemberGrant = "010f0f00dee300000000010013e89d0101";

 /// <summary>The member opens that corpse (1338), and the 2374 listing carries the member's own GID.</summary>
    private const string RetailMember2374 =
        "01363600aee20000000013e89d01000101bb5e620296668c0da83d040e0100000000000000000000000000000000000181d9e7a7b3b6e00d";

    [Fact]
    public void MemberGrantAndList_AreRetailsFrames()
    {
        Assert.Equal(RetailMemberGrant, ToHex(CombatPackets.BuildLootingAuthority2471(RetailCorpse)));
        var item = new GroundItemService.CorpseItem(0x0E043DA80D8C6696UL, 0x02625EBBu, 1);
        Assert.Equal(RetailMember2374, ToHex(PickupNotifyPackets.BuildHeartBeatItem2374(RetailCorpse, [item], [RetailMemberGid])));
    }

    [Fact]
    public void SharedCorpse_EveryLooterOpensItAsItself_AndOnlyLootersTakeFromIt()
    {
        const uint map = 910_301, corpse = 0x00ABCD13, killer = 51, member = 52, stranger = 53;
        const ulong killerGid = 0x0DE0B6B3A7E7D000UL, memberGid = RetailMemberGid;
        var died = DateTime.UtcNow;
        GroundItemService.BeginCorpse(map, corpse, 50000001, died, killer, killerGid, nowUtc: died,
            sharedLooters: [new GroundItemService.CorpseLooter(member, memberGid)]);
        var a = GroundItemService.AddCorpseItem(map, corpse, 0x02625EBBu, 1)!;
        var b = GroundItemService.AddCorpseItem(map, corpse, 30000205, 2)!;
        try
        {
            Assert.True(GroundItemService.TryGetCorpseLoot(map, corpse, member, died, out var loot));
            Assert.Equal(new[] { new GroundItemService.CorpseLooter(killer, killerGid), new GroundItemService.CorpseLooter(member, memberGid) },
                loot.Looters);
            Assert.False(GroundItemService.TryGetCorpseLoot(map, corpse, stranger, died, out _));

            
            Assert.True(GroundItemService.TryBuildCorpseList2374(map, corpse, member, died, out var memberList));
            Assert.Equal(ToHex(PickupNotifyPackets.BuildHeartBeatItem2374(corpse, [a, b], [memberGid])), ToHex(memberList));
            Assert.True(GroundItemService.TryBuildCorpseList2374(map, corpse, killer, died, out var killerList));
            Assert.Equal(ToHex(PickupNotifyPackets.BuildHeartBeatItem2374(corpse, [a, b], [killerGid])), ToHex(killerList));
            Assert.False(GroundItemService.TryBuildCorpseList2374(map, corpse, stranger, died, out _));

            Assert.False(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, stranger, died, out _, out _));
            Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, member, died, out var took, out var left));
            Assert.Equal((a, 1), (took, left));
            Assert.Equal(1, ItemPickUpHandler.ItemsLooterCanStillTake(map, corpse, killer, died));
            Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, b.Uid, killer, died, out _, out left));
            Assert.Equal(0, left);
            Assert.False(GroundItemService.HasCorpseLoot(map, corpse, died));
        }
        finally
        {
            GroundItemService.RemoveCorpse(map, corpse);
        }
    }

 /// <summary>Loot modes 2, 3 and 7 share the corpse. Only these modes occur in party rosters, so no other mode shares it.</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    public void LootModes_ThatShareTheCorpse(int lootMode, bool shares)
        => Assert.Equal(shares, GroundItemService.SharesCorpseLoot((byte)lootMode));

 /// <summary>A kill in a party, run through LootService.ProcessNpcCorpseAsync on a real ProudServer. The member on the killer's map gets its 2471 grant before the 2392, and a member on another map gets nothing. The NPC has no drop table, so the corpse holds nothing and both grants are withdrawn after the 2392. In a party whose loot mode shares nothing, the member receives only the map's 2392.</summary>
    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(0, 0, false)]
    public async Task PartyKill_GrantsTheMembersOnTheMap_BeforeThe2392(int lootMode, int partyType, bool shared)
    {
        await using var rig = await CorpseShareRig.StartAsync(3);
        var (npcs, handle) = CorpseShareRig.OneDeadNpc(out var map);
        var killer = rig.AddPlayer(0, charId: 61, map);
        var member = rig.AddPlayer(1, charId: 62, map);
        var away = rig.AddPlayer(2, charId: 63, map + 1);
        var party = new PartyState
        {
            PartyId = 9001, LeaderSessionId = killer.SessionId, PartyType = (byte)partyType, ItemRule = (byte)lootMode,
            ItemQuality = (byte)(lootMode == 2 ? 2 : 0),
            MemberSessionIds = [killer.SessionId, member.SessionId, away.SessionId],
        };
        rig.Party.Track(party);
        var loot = rig.Loot(npcs);

        PlayerState[] sharers = shared ? [member] : [];
        Assert.Equal(sharers, loot.SharedCorpseLooters(killer, handle));

        await loot.ProcessNpcCorpseAsync(rig.Sessions[0], killer, handle, CorpseShareRig.Spawn.AssetIndex);
        await rig.SendMarkersAsync();

        var grant = ToHex(CombatPackets.BuildLootingAuthority2471(handle));
        var revoke = ToHex(CombatPackets.BuildLootingAuthority2471(handle, granted: false));
        var dead = ToHex(ActorVitalsPackets.BuildDeadNotify2392(handle, killer.EntityId));
        string[] killerFrames = [$"2392:{dead}", $"2471:{revoke}"];
        string[] memberFrames = shared ? [$"2471:{grant}", $"2392:{dead}", $"2471:{revoke}"] : [$"2392:{dead}"];
        Assert.Equal(killerFrames, await rig.FramesUntilMarkerAsync(0));
        Assert.Equal(memberFrames, await rig.FramesUntilMarkerAsync(1));
        Assert.Empty(await rig.FramesUntilMarkerAsync(2));
    }
}

/// <summary>The xUnit collection for every test class that drives a <see cref="CorpseShareRig"/>. Its members run one at a time, never in parallel with other classes.</summary>
/// <remarks>Rigs register corpses under the same process-wide registry keys, so the classes that use them must not overlap. The class is public because xUnit requires that of a collection definition.</remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CorpseShareRigCollection
{
 /// <summary>What each member class passes to <c>[Collection]</c>.</summary>
    public const string Name = "CorpseShareRig";
}

/// <summary>A ProudServer with <c>n</c> connected clients and the zone services used by a kill's corpse and by a pickup from it. Test classes that start one join <see cref="CorpseShareRigCollection"/>.</summary>
internal sealed class CorpseShareRig : IAsyncDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

 /// <summary>The one spawn every test NPC comes from (asset 28580000, a monster with no drop table here).</summary>
    public static readonly SpawnEntry Spawn = new()
    {
        MapId = 910_400, Index = 11, PosX = 447.5f, PosY = 321.1f, PosZ = 12.5f, Rot = 300f, AssetIndex = 28580000,
    };

    private readonly ProudServer _server;
    private readonly Socket[] _clients;
    private readonly ServiceProvider _services;

    public ProudSession[] Sessions { get; }
    public PlayerStateManager Players { get; } = new(new NoDirtySink());
    public BroadcastService Broadcast { get; }
    public PartyService Party { get; }
    public GameDataStore Data { get; } = new();

 /// <summary>What a handler's request scope resolves (the corpse's NpcStateManager is set by <see cref="Loot"/>).</summary>
    public IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

 /// <summary>The root provider behind <see cref="ScopeFactory"/>.</summary>
    public IServiceProvider Services => _services;

    private NpcStateManager? _npcs;

    private CorpseShareRig(ProudServer server, Socket[] clients, ProudSession[] sessions)
    {
        _server = server;
        _clients = clients;
        Sessions = sessions;
        Broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
        Broadcast.SetServer(server);
        Party = new PartyService(Players, Broadcast, NullLogger<PartyService>.Instance);
        var collection = new ServiceCollection();
        collection.AddSingleton(Players);
        collection.AddSingleton(Broadcast);
        collection.AddSingleton(Party);
        collection.AddSingleton(Data);
        collection.AddSingleton<NpcStateManager>(_ => _npcs ?? throw new InvalidOperationException("call Loot(npcs) first"));
        _services = collection.BuildServiceProvider();
    }

 /// <summary>The LootService a kill goes through, over <paramref name="npcs"/> (also what handlers resolve).</summary>
    public LootService Loot(NpcStateManager npcs)
    {
        _npcs = npcs;
        var combat = new CombatService(Players, npcs, Broadcast, Data, NullLogger<CombatService>.Instance);
        return new LootService(Data, Broadcast, combat, npcs, Party, Players, ScopeFactory, NullLogger<LootService>.Instance);
    }

 /// <summary>An NPC of <see cref="Spawn"/> just killed: its state manager and its handle (the corpse).</summary>
    public static (NpcStateManager Npcs, uint Handle) OneDeadNpc(out uint map)
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(Spawn.MapId, [Spawn]);
        var handle = npcs.AllocateHandle(Spawn.MapId, Spawn.Index);
        Assert.True(npcs.ApplyDamage(handle, int.MaxValue).Died);
        map = Spawn.MapId;
        return (npcs, handle);
    }

    public static async Task<CorpseShareRig> StartAsync(int n)
    {
        var server = new ProudServer("test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var clients = new Socket[n];
        var sessions = new ProudSession[n];
        for (int i = 0; i < n; i++)
        {
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<ProudSession, Task> onConnect = s => { connected.TrySetResult(s); return Task.CompletedTask; };
            server.SessionConnected += onConnect;
            clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clients[i].ConnectAsync(server.LocalEndPoint!);
            sessions[i] = await connected.Task.WaitAsync(Generous);
            server.SessionConnected -= onConnect;
        }
        return new CorpseShareRig(server, clients, sessions);
    }

    public PlayerState AddPlayer(int i, uint charId, uint map, ulong gid = 0, uint handle = 0)
    {
        var sid = Sessions[i].SessionId;
        var p = new PlayerState
        {
            SessionId = sid, EntityId = handle != 0 ? handle : PlayerStateManager.BuildActorHandle(sid), CharacterId = charId,
            CharacterUid = gid != 0 ? gid : 0x0DE0B6B3A7000000UL + charId, Name = "P" + charId, MapId = map,
        };
        Players.AddPlayer(sid, p);
        return p;
    }

 /// <summary>A 0x5A OnLevelUp marker to every client, which <see cref="FramesUntilMarkerAsync"/> stops at.</summary>
    public async Task SendMarkersAsync()
    {
        foreach (var s in Sessions)
            await s.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
    }

 /// <summary>"op:hex" of each RMI client <paramref name="i"/> receives before the 0x5A marker.</summary>
    public async Task<List<string>> FramesUntilMarkerAsync(int i)
    {
        var frames = new List<string>();
        while (true)
        {
            var head = await ReadExactAsync(i, 3);
            Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
            var lengthBytes = await ReadExactAsync(i, head[2]);
            int length = head[2] switch
            {
                1 => lengthBytes[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
            };
            var data = await ReadExactAsync(i, length);
            if (data.Length < 3 || data[0] != 0x01) continue;
            var op = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
            var body = data[3..];
            if (op == ZoneS2COpcodes.OnLevelUp && body is [0x5A]) return frames;
            if (op == ZoneS2COpcodes.OnNPC_Out) continue; 
            frames.Add($"{op}:{Convert.ToHexString(body).ToLowerInvariant()}");
        }
    }

    private async Task<byte[]> ReadExactAsync(int i, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _clients[i].ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
            if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
            read += n;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        await _server.StopAsync();
        await _services.DisposeAsync();
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }
}
