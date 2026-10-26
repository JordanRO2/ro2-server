using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Covers the two in-world triggers of 2595 OnPcInitTrait: the level-up burst from <see cref="CombatService.GrantExpAsync"/>,
/// and the stat-reset scroll (RMI 1323, ItemUseHandler).
/// The level-up burst is 2595, 2498, 2394, 2395 and 2542, followed by 2726 when applicable. The 2498 is the sheet at the new level,
/// and the 2394 refills HP and SP to that sheet's maximum. The stat-reset scroll is answered with 2595, 2498 and 2329.
/// </summary>
public sealed class LevelUpTraitBurstTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadFullStoreAsync);

    
    
    
    
    
    private const string Retail2595 =
        "028c008c0096e300000000000000000000010001000100010001000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000803f3d080000e50100000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000";
    private const string Retail2498 =
        "028c008c00cce30000000014090000e5012e00200016001d002d000000000000000000000000000000160024010000" +
        "c80000000000320000000100000000000000000000000000000040403d080000e50104000200000000000000870021" +
        "0020002e00000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000";
    private const string Retail2394 = "01262600c9e2112a050717ce640000000000001409000014090000e5010000e50100000005000000";
    private const string Retail2395 = "010f0f00cae200000000112a0507170000";
    private const string Retail2542 = "010909000de4112a050716";
    private const string Retail2726 = "1902000000000000";

    private const uint RetailHandle = 0x07052A11u;

 /// <summary>
 /// Each message of the level-up burst follows from the 2498 and the levels crossed, as composed by SendLevelUpBurstAsync: the 2595 is built from the 2498;
 /// the 2394 refills to the 2498 maximum, with the stat points of every level crossed and the EXP within the final level; one 2395 and one 2542 are sent for the final level;
 /// and one 2726 uses the last completed level's Base_Exp_Next divided by 100, not the sum over all levels.
 /// </summary>
    [Fact]
    public async Task RetailMultiLevelBurst_FollowsFromItsSheetAndTheLevelsCrossed()
    {
        var store = await Store.Value;
        var trait = Convert.FromHexString(Retail2498);
        var blob = trait.AsSpan(3);
        uint maxHp = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        ushort maxSp = BinaryPrimitives.ReadUInt16LittleEndian(blob[12..]);
        uint curHp = BinaryPrimitives.ReadUInt32LittleEndian(blob[72..]);
        Assert.True(curHp < maxHp); 

        Assert.Equal(Convert.FromHexString(Retail2595),
            TraitPackets.BuildPcInitTrait2595(new Character { Name = "T" }, trait));
        Assert.Equal(Convert.FromHexString(Retail2394),
            CombatPackets.BuildLevelUp2394(RetailHandle, 23, 25806, maxHp, maxHp, maxSp, maxSp,
                CombatPackets.StatPointsForLevelUp(18, 23)));
        Assert.Equal(Convert.FromHexString(Retail2395), CombatPackets.BuildLevelUpNotify2395(RetailHandle, 23));
        Assert.Equal(Convert.FromHexString(Retail2542), TalentNotifyPackets.BuildOnGetTalentPoint(RetailHandle, 22));

        var lastLevel = GuildBribePackets.LevelUpBribeAmount((ulong)store.GetExpForNextLevel(22));
        Assert.Equal(Convert.FromHexString(Retail2726), GuildBribePackets.BuildNfyGuildBribePCLevelUp(lastLevel));
        ulong sum = 0;
        for (byte l = 18; l <= 22; l++) sum += GuildBribePackets.LevelUpBribeAmount((ulong)store.GetExpForNextLevel(l));
        Assert.NotEqual(lastLevel, sum);
    }

    
 /// <summary>
 /// A grant across three levels on a real ProudServer produces one burst in the expected order: the 2498 built from the sheet at the new level
 /// (EffectService.RefreshPcTraitSheet) with the previous HP, the 2595 built from it, the 2394 at that sheet's maximum with current equal to max
 /// and the stat points of all three levels, the leveler's own 2395, and one 2542. A marker sent afterwards is the next frame, with no second 2394.
 /// </summary>
    [Fact]
    public async Task GrantAcrossThreeLevels_SendsOneBurstInRetailsOrder()
    {
        var store = await Store.Value;
        await using var rig = await Rig.StartAsync(store, withSheet: true);
        var character = new Character
        {
            Id = 904_201, Name = "Leveler", Job = 4, Level = 20, Str = 40, Agi = 20, Int = 10, Wis = 30, Vit = 50, Luk = 1,
        };
        var player = rig.AddPlayer(character, hp: 300, mp: 20);
        ulong needed = 0;
        for (byte l = 20; l < 23; l++) needed += (ulong)store.GetExpForNextLevel(l);
        await rig.Combat.GrantExpAsync(player, needed + 7);
        await rig.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A }); 

        var (op1, initTrait) = await rig.NextRmiAsync();
        var (op2, trait) = await rig.NextRmiAsync();
        var (op3, levelUp) = await rig.NextRmiAsync();
        var (op4, notify) = await rig.NextRmiAsync();
        var (op5, talent) = await rig.NextRmiAsync();
        var (op6, marker) = await rig.NextRmiAsync();
        Assert.Equal(new[] { ZoneS2COpcodes.OnPcInitTrait, ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnLevelUp,
            ZoneS2COpcodes.OnLevelUpNotify, ZoneS2COpcodes.OnGetTalentPoint, ZoneS2COpcodes.OnLevelUp },
            new[] { op1, op2, op3, op4, op5, op6 });
        Assert.Equal(new byte[] { 0x5A }, marker);

        var blob = trait.AsSpan(3);
        uint maxHp = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        ushort maxSp = BinaryPrimitives.ReadUInt16LittleEndian(blob[12..]);
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32LittleEndian(blob[72..])); 
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(blob[76..]));
        Assert.True(maxHp > 300);

        Assert.Equal(23, character.Level); 
        Assert.Equal(TraitPackets.BuildFullPcTrait2498(character, store, Array.Empty<InventoryItem>(), 300, 20), trait);
        Assert.Equal(TraitPackets.BuildPcInitTrait2595(character, trait), initTrait);
        Assert.Equal(CombatPackets.BuildLevelUp2394(player.EntityId, 23, 7, maxHp, maxHp, maxSp, maxSp,
            CombatPackets.StatPointsForLevelUp(20, 23)), levelUp);
        Assert.Equal(CombatPackets.BuildLevelUpNotify2395(player.EntityId, 23), notify);
        Assert.Equal(TalentNotifyPackets.BuildOnGetTalentPoint(player.EntityId, 22), talent);

        Assert.Equal((23, 7UL), ((int)player.Level, player.Exp));
        Assert.Equal(((int)maxHp, (int)maxHp, (int)maxSp, (int)maxSp), (player.Hp, player.MaxHp, player.Mp, player.MaxMp));
    }

 /// <summary>
 /// Without a scope factory, the burst carries no 2595 or 2498, since a sheet without the worn gear would remove every gear bonus.
 /// It is still one burst for the whole grant, refilled to the base-stat maximum of the final level.
 /// </summary>
    [Fact]
    public async Task WithoutTheSheet_StillOneBurstForTheWholeGrant()
    {
        var store = await Store.Value;
        await using var rig = await Rig.StartAsync(store, withSheet: false);
        var character = new Character { Id = 904_202, Name = "NoSheet", Job = 4, Level = 10 };
        var player = rig.AddPlayer(character, hp: 50, mp: 5);
        ulong needed = (ulong)store.GetExpForNextLevel(10) + (ulong)store.GetExpForNextLevel(11);
        await rig.Combat.GrantExpAsync(player, needed);
        await rig.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        var ops = new List<ushort>();
        byte[] levelUp = Array.Empty<byte>();
        while (true)
        {
            var (op, body) = await rig.NextRmiAsync();
            if (op == ZoneS2COpcodes.OnLevelUp && body is [0x5A]) break;
            if (op == ZoneS2COpcodes.OnLevelUp) levelUp = body;
            ops.Add(op);
        }
        Assert.Equal(new[] { ZoneS2COpcodes.OnLevelUp, ZoneS2COpcodes.OnLevelUpNotify, ZoneS2COpcodes.OnGetTalentPoint }, ops);
        Assert.Equal(12, player.Level);
        Assert.Equal(CombatPackets.BuildLevelUp2394(player.EntityId, 12, 0, (uint)player.MaxHp, (uint)player.MaxHp,
            (uint)player.MaxMp, (uint)player.MaxMp, CombatPackets.StatPointsForLevelUp(10, 12)), levelUp);
    }

    
    
    
    
    private const string ResetRequest1323 = "01151500a5e21115f006e811840da83d040e1115f00601";
    private const string ResetRetail2595 =
        "028c008c0096e300000000000000000000010001000100010001000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000803f61090000780000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000";
    private const string ResetRetail2498 =
        "028c008c00cce30000000061090000780018000b0001000800170000000000000000000000000000004b0030000000" +
        "000000000000320000000100000000000000000000000000000040406109000078000300010000000000000046000c" +
        "000b001800000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000";
    private const string ResetRetail2329 = "01161600a6e21115f00600003901e811840da83d040e0001";

 /// <summary>
 /// The 2595 built by TraitPackets.BuildPcInitTrait2595 for a reset character (no unallocated points, one per primary stat),
 /// the 2498 sent after it with current HP and SP restored, and the 2329 naming the used scroll's cell.
 /// </summary>
    [Fact]
    public void RetailStatReset_2595IsBuiltFromTheResetSheet()
    {
        Assert.Equal(Convert.FromHexString(ResetRetail2595),
            TraitPackets.BuildPcInitTrait2595(new Character { Name = "T" }, Convert.FromHexString(ResetRetail2498)));
        Assert.Equal(Convert.FromHexString(ResetRetail2329),
            CombatPackets.BuildUseItem2329(0x06F01511u, slotIndex: 57, bagId: 1, itemUid: 0x0E043DA80D8411E8UL,
                remainingCount: 0, useSource: 1));
    }

 /// <summary>
 /// A stat-reset scroll used through ItemUseHandler refunds the allocation. The answer is 2595, then 2498, then 2329, with nothing in between.
 /// The 2595 carries one point per primary stat and the 2498 carries the vitals. The 2329 matches the reference frame byte for byte.
 /// </summary>
    [Fact]
    public async Task StatResetScroll_Answers2595Then2498BeforeThe2329()
    {
        var store = await Store.Value;
        var scroll = new InventoryItem
        {
            Id = 0x0E043DA80D8411E8UL, CharacterId = 904_203, ItemId = 60000061, Count = 1, Slot = 7, StorageType = 0,
        };
        await using var rig = await Rig.StartAsync(store, withSheet: true, scroll);
        var character = new Character
        {
            Id = 904_203, Name = "Resetter", Job = 4, Level = 40, Str = 140, Agi = 20, Int = 10, Wis = 30, Vit = 50, Luk = 1,
            BonusStr = 100,
        };
        var player = rig.AddPlayer(character, hp: 900, mp: 60);
        player.EntityId = 0x06F01511u;
        var handler = new RO2.Server.Zone.Handlers.Rmi.ItemUseHandler(rig.ScopeFactory,
            NullLogger<RO2.Server.Zone.Handlers.Rmi.ItemUseHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Convert.FromHexString(ResetRequest1323));
        await rig.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A }); 

        var (op1, initTrait) = await rig.NextRmiAsync();
        var (op2, trait) = await rig.NextRmiAsync();
        var (op3, used) = await rig.NextRmiAsync();
        var (op4, marker) = await rig.NextRmiAsync();
        Assert.Equal(new[] { ZoneS2COpcodes.OnPcInitTrait, ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnUseItem,
            ZoneS2COpcodes.OnLevelUp }, new[] { op1, op2, op3, op4 });
        Assert.Equal(new byte[] { 0x5A }, marker);

        Assert.Equal(0, character.BonusStr);                     
        Assert.Equal(TraitPackets.BuildPcInitTrait2595(character, trait), initTrait);
        for (int i = 0; i < 5; i++)                              
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(initTrait.AsSpan(3 + 14 + 2 * i)));
        Assert.Equal(trait.AsSpan(3 + 72, 6).ToArray(), initTrait.AsSpan(3 + 72, 6).ToArray());
        Assert.Equal(Convert.FromHexString(ResetRetail2329), used);
        Assert.Empty(rig.Inventory.Items);                       
    }

    
    private static async Task<GameDataStore> LoadFullStoreAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "BaseExpTable.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/BaseExpTable.json");
    }

 /// <summary>
 /// A CombatService on a real ProudServer with one connected client whose S2C frames the test reads back. With <c>withSheet</c>,
 /// its scope factory provides an EffectService, an inventory with no worn gear, and the GameDataStore that ItemUseHandler reads from the scope.
 /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly ServiceProvider _services;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public BroadcastService Broadcast { get; }
        public CombatService Combat { get; }
        public BagInventory Inventory { get; }
        public IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

        private Rig(ProudServer server, Socket client, ProudSession session, GameDataStore data, bool withSheet,
            InventoryItem[] bag)
        {
            _server = server;
            _client = client;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            Broadcast = broadcast;
            var npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            Inventory = new BagInventory(bag);
            var collection = new ServiceCollection();
            collection.AddSingleton(new EffectService(players: null!, broadcast: null!, data: data, npcState: null!,
                loot: null!, scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            collection.AddSingleton<IInventoryRepository>(Inventory);
            collection.AddSingleton(data);
            _services = collection.BuildServiceProvider();
            Combat = withSheet
                ? new CombatService(Players, npcs, broadcast, data, NullLogger<CombatService>.Instance,
                    _services.GetRequiredService<IServiceScopeFactory>())
                : new CombatService(Players, npcs, broadcast, data, NullLogger<CombatService>.Instance);
        }

        public static async Task<Rig> StartAsync(GameDataStore data, bool withSheet, params InventoryItem[] bag)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data, withSheet, bag);
        }

        public PlayerState AddPlayer(Character character, int hp, int mp)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, CharacterId = character.Id, EntityId = 0x0B0B0011u, Name = character.Name,
                Job = character.Job, Level = character.Level, MapId = 1100, Character = character,
                Hp = hp, MaxHp = hp, Mp = mp, MaxMp = mp,
            };
            Players.AddPlayer(Session.SessionId, player);
            return player;
        }

 /// <summary>The next internal RMI frame the client receives (<c>[13 57][w][len][01][u16 op][body]</c>).</summary>
        public async Task<(ushort Op, byte[] Body)> NextRmiAsync()
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
                int n = await _client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _server.StopAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

 /// <summary>A bag with nothing worn (the sheet is the naked one), holding the items a test puts in it.</summary>
    private sealed class BagInventory(InventoryItem[] items) : IInventoryRepository
    {
        public List<InventoryItem> Items { get; } = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { Items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(Items.Count == 0 ? 1UL : Items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
