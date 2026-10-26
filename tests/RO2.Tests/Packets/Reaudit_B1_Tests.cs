using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Re-audit, unit B1: the map-move buff of the 1304 AnsMapLoading reply (Config_Def 65/66, Effect_Table
/// 60205 for 10 s): its 2499 2499 2498 2492 run, the blob+64 Miss_Rate slot of the 2498, and the miss it grants
/// against a monster's hit.
/// </summary>
/// <remarks>
/// The buff is applied in the reply to the 1304 (2499, 2499, 2498, 2492 after the 2986) and removed
/// 10.4 s later (2498, 2493, 2494).
/// </remarks>
public class Reaudit_B1_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    private const uint Pc = 0x0D790911;

    // The run after the 2986.
    private const string Run2499Trait51 = "01191900fde300000000040000003300000000401cc62deb000001";
    private const string Run2499Trait35 = "01191900fde300000000040000002300000000401c462deb000001";
    private const string Run2498 =
        "028c008c00cce3000000005beb00009f39ed097705d01bcb027710000000000000000000007701000000005e43000013de000000004237"
        + "000001000000a3010000000010270000000040405beb00009f39fe0021000000b71a9302082f7805090e0c265e0bbf000000000000000000"
        + "0000000000000000000000000000000000000000fa025c097800000000000000";
    private const string Run2492 = "01242400c7e3000000001109790d2deb0000010010270000000000000100000000001109790d";

    // The removal 10.4 s later: the same sheet with blob+64 back to 0.
    private const string Removal2498 =
        "028c008c00cce3000000005beb00009f39ed097705d01bcb027710000000000000000000007701000000005e43000013de000000004237"
        + "000001000000a3010000000000000000000040405beb00009f39fe0021000000b71a9302082f7805090e0c265e0bbf000000000000000000"
        + "0000000000000000000000000000000000000000fa025c097800000000000000";

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ---- data ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MapMoveBuff_IsConfigDef65And66()
    {
        var data = await Data.Value;
        // Config_Def 65 '맵이동시 사용되는 버프 ID' = 60205, 66 '... 시간 (단위 초)' = 10: the 2492 carries 10,000 ms.
        Assert.Equal((60205u, 10_000UL), MapMoveBuff.Of(data.ConfigDef));
        Assert.Null(MapMoveBuff.Of(Array.Empty<RO2.GameData.Tables.ConfigDef>()));
    }

    [Fact]
    public async Task BuffLines_AreRetailsTwo2499s_InTableOrder()
    {
        var data = await Data.Value;
        var effects = NewEffectService(data, new PlayerStateManager(new NoDirtySink()), broadcast: null!);
        var lines = effects.BuildTraitChangeLines(60205, 1);
        Assert.Equal(new[] { Run2499Trait51, Run2499Trait35 }, lines.Select(Hex).ToArray());
    }

    // ---- the 2498 slot -----------------------------------------------------------------------------------------

    [Fact]
    public void Fold_PutsTheKind4MissRateRawAtBlob64_RetailRunSheetFromTheRemovalOne()
    {
        // The removal sheet with the buff's trait-35 +10,000 folded in is the run's sheet, byte for byte: the two
        // frames differ only at blob+64. Current HP/SP are the frame's own (blob+72 / blob+76).
        var removal = Convert.FromHexString(Removal2498);
        var blob = removal.AsSpan(3);
        int hp = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitCurHpOffset..]);
        int sp = BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitCurSpOffset..]);
        var modifiers = new EffectService.EffectSheetModifiers(
            new Dictionary<byte, double>(), new Dictionary<byte, double> { [EffectService.MissRateTraitId] = 10_000 });

        Assert.Equal(Run2498, Hex(EffectService.FoldSheetModifiers(removal, modifiers, hp, sp)));
        Assert.Equal(Removal2498, Hex(EffectService.FoldSheetModifiers(removal, EffectService.EffectSheetModifiers.None, hp, sp)));
    }

    [Fact]
    public async Task InWorldSheet_WithTheBuff_DiffersFromThePlainOneOnlyAtBlob64()
    {
        var data = await Data.Value;
        var players = new PlayerStateManager(new NoDirtySink());
        var effects = NewEffectService(data, players, broadcast: null!);
        var player = NewPlayer(sessionId: 1);
        var plain = effects.BuildInWorldPcTrait(player, player.Character!, Array.Empty<InventoryItem>());
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 60205, EffectLevel = 1, TargetHandle = Pc });
        var buffed = effects.BuildInWorldPcTrait(player, player.Character!, Array.Empty<InventoryItem>());

        var differing = Enumerable.Range(0, plain.Length).Where(i => plain[i] != buffed[i]).Select(i => i - 3).ToArray();
        Assert.Equal(new[] { EffectService.MissRateBlobOffset, EffectService.MissRateBlobOffset + 1 }, differing);
        Assert.Equal(10_000, BinaryPrimitives.ReadUInt16LittleEndian(buffed.AsSpan(3 + EffectService.MissRateBlobOffset)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(3 + EffectService.MissRateBlobOffset)));
        Assert.Equal(EffectService.FullMissRate, effects.MissRateOf(player));
    }

    [Fact]
    public async Task MissRate_IsZeroWithoutTheBuff_AndIgnoresKind3Rows()
    {
        var data = await Data.Value;
        var effects = NewEffectService(data, new PlayerStateManager(new NoDirtySink()), broadcast: null!);
        var player = NewPlayer(sessionId: 1);
        Assert.Equal(0, effects.MissRateOf(player));
        // 92260 '회피율증가' is a Trait_Kind 3 trait-35 row: not folded, not a miss rate (see EffectService.MissRateBlobOffset).
        player.ActiveEffects.Add(new ActiveEffect { EffectId = 92260, EffectLevel = 1, TargetHandle = Pc });
        Assert.Equal(0, effects.MissRateOf(player));
        var sheet = effects.BuildInWorldPcTrait(player, player.Character!, Array.Empty<InventoryItem>());
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(sheet.AsSpan(3 + EffectService.MissRateBlobOffset)));
    }

    // ---- the run on the wire -----------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyMapMoveBuff_SendsRetailsRun_AndRecordsTheTenSecondEffect()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer();

        var before = DateTime.UtcNow;
        await rig.Effects.ApplyMapMoveBuffAsync(rig.Session, player, player.Character!, Array.Empty<InventoryItem>());
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.Equal(4, frames.Count);
        Assert.Equal((ZoneS2COpcodes.OnPcTraitChange, Run2499Trait51), frames[0]);
        Assert.Equal((ZoneS2COpcodes.OnPcTraitChange, Run2499Trait35), frames[1]);
        Assert.Equal(ZoneS2COpcodes.OnPcTrait, frames[2].Op);
        var sheet = Convert.FromHexString(frames[2].Body);
        Assert.Equal(TraitPackets.PcTraitBodyLen, sheet.Length);
        Assert.Equal(10_000, BinaryPrimitives.ReadUInt16LittleEndian(sheet.AsSpan(3 + EffectService.MissRateBlobOffset)));
        // The 2492 is retail's byte for byte: handle, 60205, value 1, 10,000 ms, stack 1, skill 0, mode 0, caster self.
        Assert.Equal((ZoneS2COpcodes.OnAddEffect, Run2492), frames[3]);

        ActiveEffect effect;
        lock (player.ActiveEffects)
            effect = Assert.Single(player.ActiveEffects);
        Assert.Equal(60205u, effect.EffectId);
        Assert.Equal(10_000UL, effect.DurationMs);
        Assert.NotNull(effect.ExpiresAt);
        Assert.InRange(effect.ExpiresAt!.Value, before.AddSeconds(10), DateTime.UtcNow.AddSeconds(10));

        // A second map load inside the 10 s restarts it rather than stacking a second copy.
        await rig.Effects.ApplyMapMoveBuffAsync(rig.Session, player, player.Character!, Array.Empty<InventoryItem>());
        await rig.FramesUntilMarkerAsync();
        lock (player.ActiveEffects)
            Assert.Single(player.ActiveEffects);
    }

    [Fact]
    public async Task CancelOfTheBuff_SendsTheRemovalSheetWithBlob64BackToZero()
    {
        // 60205 is Picking_Delete_Type 1, so a 1463 may end it early; the removal run is 2498 -> 2493 -> 2494 with the
        // sheet's blob+64 back to 0 (2493 and 2494 match the wire form byte for byte).
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer();
        await rig.Effects.ApplyMapMoveBuffAsync(rig.Session, player, player.Character!, Array.Empty<InventoryItem>());
        await rig.FramesUntilMarkerAsync();

        Assert.True(await rig.Effects.CancelPlayerEffectAsync(player, 60205));
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.Equal(3, frames.Count);
        Assert.Equal(ZoneS2COpcodes.OnPcTrait, frames[0].Op);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(
            Convert.FromHexString(frames[0].Body).AsSpan(3 + EffectService.MissRateBlobOffset)));
        Assert.Equal((ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000002deb00000000000000000000"), frames[1]);
        Assert.Equal((ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000002deb00001109790d0000000000000000"), frames[2]);
        Assert.Equal(0, rig.Effects.MissRateOf(player));
    }

    // ---- what the buff grants ----------------------------------------------------------------------------------

    [Fact]
    public async Task MonsterHit_OnAPcHoldingTheBuff_IsAKind2MissWithNoWear()
    {
        // A monster's 2696 on a PC inside a 60205 window is kind 2, damage 0, with no 2503 after it. The 2696 layout:
        // blob+9 kind, blob+10 damage, blob+38 victim, blob+42 its HP.
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer();
        var npc = rig.AddMonster(atk: 500);
        player.IsInCombat = true;
        player.TargetEntityId = npc.EntityOid;
        await rig.Effects.ApplyMapMoveBuffAsync(rig.Session, player, player.Character!, Array.Empty<InventoryItem>());
        await rig.FramesUntilMarkerAsync();
        int hpBefore = player.Hp;

        await rig.RunNpcCombatPassAsync();
        var frames = await rig.FramesUntilMarkerAsync();

        var hit = Convert.FromHexString(Assert.Single(frames, f => f.Op == ZoneS2COpcodes.NfyAttackDamage).Body).AsSpan(2);
        Assert.Equal(CombatPackets.DamageKindMiss, hit[9]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hit[10..]));
        Assert.Equal(Pc, BinaryPrimitives.ReadUInt32LittleEndian(hit[38..]));
        Assert.Equal((uint)hpBefore, BinaryPrimitives.ReadUInt32LittleEndian(hit[42..]));
        Assert.DoesNotContain(frames, f => f.Op == ZoneS2COpcodes.OnUpdateItemDurability);
        Assert.Equal(hpBefore, player.Hp);
        Assert.False(player.IsDead);
    }

    [Fact]
    public async Task MonsterHit_WithoutTheBuff_StillLands()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer();
        var npc = rig.AddMonster(atk: 500);
        player.IsInCombat = true;
        player.TargetEntityId = npc.EntityOid;
        int hpBefore = player.Hp;

        await rig.RunNpcCombatPassAsync();
        var frames = await rig.FramesUntilMarkerAsync();

        var hit = Convert.FromHexString(Assert.Single(frames, f => f.Op == ZoneS2COpcodes.NfyAttackDamage).Body).AsSpan(2);
        Assert.NotEqual(CombatPackets.DamageKindMiss, hit[9]);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(hit[10..]) > 0);
        Assert.True(player.Hp < hpBefore);
    }

    // ---- shared -------------------------------------------------------------------------------------------------

    private static EffectService NewEffectService(GameDataStore data, PlayerStateManager players, BroadcastService broadcast)
        => new(players, broadcast, data, npcState: null!, loot: null!, scopeFactory: null!, NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());

    private static PlayerState NewPlayer(uint sessionId) => new()
    {
        SessionId = sessionId, EntityId = Pc, CharacterId = 915_501, Name = "Arrival", Level = 60, MapId = 1100,
        MapLoaded = true, Hp = 5000, MaxHp = 5000, Mp = 500, MaxMp = 500,
        Character = new Character { Id = 915_501, Name = "Arrival", Job = 1, Level = 60 },
    };

    private static async Task<GameDataStore> LoadDataAsync()
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
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    /// <summary>An empty bag with nothing worn.</summary>
    private sealed class NoGearInventory : IInventoryRepository
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

    /// <summary>
    /// A real <see cref="ProudSession"/> on a loopback <see cref="ProudServer"/>, read from the client end, with the
    /// singletons the buff and the monster pass resolve: GameDataStore, BroadcastService, EffectService, an
    /// NpcStateManager, DurabilityService, NpcChatService and an empty inventory, and no database.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly ServiceProvider _services;
        private readonly NpcStateManager _npcs;
        private readonly NpcCombatService _combat;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public EffectService Effects { get; }

        private Rig(ProudServer server, Socket client, ProudSession session, GameDataStore data)
        {
            _server = server;
            _client = client;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            _npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var players = Players;
            var npcs = _npcs;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton(npcs);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: npcs, loot: null!,
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            collection.AddSingleton(sp => new DurabilityService(data, NullLogger<DurabilityService>.Instance,
                sp.GetRequiredService<IServiceScopeFactory>(), broadcast));
            collection.AddSingleton(_ => new NpcChatService(data, npcs, players, broadcast, NullLogger<NpcChatService>.Instance));
            _services = collection.BuildServiceProvider();
            Effects = _services.GetRequiredService<EffectService>();
            _combat = new NpcCombatService(npcs, players, broadcast, _services.GetRequiredService<DurabilityService>(),
                _services.GetRequiredService<NpcChatService>(),
                new PcCombatStateService(players, broadcast, NullLogger<PcCombatStateService>.Instance),
                _services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<NpcCombatService>.Instance);
        }

        public static async Task<Rig> StartAsync(GameDataStore data)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data);
        }

        /// <summary>An in-world PC on map 1100, alone there, registered for the broadcasts.</summary>
        public PlayerState AddPlayer()
        {
            var player = NewPlayer(Session.SessionId);
            Players.AddPlayer(Session.SessionId, player);
            return player;
        }

        /// <summary>A hostile monster on the PC's map and spot, tracked by the NpcStateManager.</summary>
        public NpcRuntimeState AddMonster(int atk)
        {
            var spawn = new SpawnEntry { MapId = 1100, Index = 9_201, AssetIndex = 0, PosX = 0f, PosY = 0f };
            _npcs.TrackSpawns(1100, [spawn]);
            var npc = _npcs.GetAliveNpcs(1100).Single();
            npc.Camp = 2;
            npc.Atk = atk;
            npc.Level = 1;
            npc.PosX = 0f;
            npc.PosY = 0f;
            return npc;
        }

        /// <summary>One retaliation pass of <see cref="NpcCombatService"/>, awaited (its private TickAsync; Tick fires
        /// it off the loop thread).</summary>
        public Task RunNpcCombatPassAsync()
        {
            var tick = typeof(NpcCombatService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)tick.Invoke(_combat, new object[] { CancellationToken.None })!;
        }

        private async Task<(ushort Op, string Body)> NextRmiHexAsync()
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
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)),
                        Convert.ToHexString(data, 3, data.Length - 3).ToLowerInvariant());
            }
        }

        /// <summary>Sends a marker frame (opcode 0, body <c>7e57</c>) and returns, in order, every RMI frame the client
        /// receives before it.</summary>
        public async Task<List<(ushort Op, string Body)>> FramesUntilMarkerAsync()
        {
            await Session.SendInternalRmiAsync(0, new byte[] { 0x7E, 0x57 });
            var frames = new List<(ushort Op, string Body)>();
            while (true)
            {
                var frame = await NextRmiHexAsync();
                if (frame == (0, "7e57")) return frames;
                frames.Add(frame);
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
            await _services.DisposeAsync();
            await _server.StopAsync();
        }
    }
}
