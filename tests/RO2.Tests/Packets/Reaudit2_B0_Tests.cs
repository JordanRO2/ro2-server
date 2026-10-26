using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Re-audit 2, unit B0: the 205-byte character head's bind index (head+105), facing (head+107) and
/// bonus-stat block (head+151..164) in 2310 / 2391 / 2487; the 2498's auto-attack reach (blob+68 = 1 + weapon range);
/// the warp burst without 2497 / 2435; and a lethal DoT tick on a player running the death sequence.
/// </summary>
/// <remarks>
/// The reasoning behind each field is documented on the writers (WorldEntryPackets.WriteCharacterHead205 /
/// FacingByte / BuildPcTrait2498FromDonor, EffectService.KillByDotAsync).
/// The head and death-sequence tests pin this server's layout and order; they are not byte comparisons with a retail
/// frame, except where a retail body is quoted.
/// </remarks>
public class Reaudit2_B0_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    /// <summary>
    /// A sample 2498 OnPcTrait body, used as the builder's template: its blob+68 (body+71) is 3.0,
    /// a melee weapon's reach.
    /// </summary>
    private const string Retail2498 =
        "028c008c00cce3000000005beb00009f39ed097705d01bcb027710000000000000000000007701000000005e43000013de000000004237"
        + "000001000000a3010000000010270000000040405beb00009f39fe0021000000b71a9302082f7805090e0c265e0bbf000000000000000000"
        + "0000000000000000000000000000000000000000fa025c097800000000000000";

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ---- head+107: the facing byte ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(MathF.PI / 2, 45)]           // 90 deg
    [InlineData(MathF.PI, 90)]               // 180 deg
    [InlineData(-MathF.PI / 2, 135)]         // -90 deg = 270 deg
    [InlineData(3.71f, 106)]                 // 212.57 deg: the top of the 2750 +68 range
    [InlineData(6.2830f, 0)]                 // 359.99 deg rounds to 180, which wraps to 0
    [InlineData(0.8265199f, 24)]             // 47.36 deg / 2 = 23.68: round-to-nearest (the unproven choice)
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    public void FacingByte_IsTwoDegreeSteps_0To179(float yaw, byte expected)
    {
        // (deg(yaw) mod 360) / 2, rounded, mod 180: the value never reaches 180. Rounding is used rather than
        // flooring, as it matches the wire values more closely.
        Assert.Equal(expected, CharacterHeadRules.FacingByte(yaw));
    }

    [Fact]
    public void FacingByte_NeverReaches180()
    {
        for (int i = -4000; i <= 4000; i++)
            Assert.InRange(CharacterHeadRules.FacingByte(i * 0.00314159f), (byte)0, (byte)179);
    }

    // ---- 2310 head: bind index, facing, bonus block ---------------------------------------------------------------

    private static Character Hero() => new()
    {
        Id = 915_610, Name = "Binder", Job = 1, Level = 30, Gender = 1, Race = 1,
        BonusStr = 4, BonusAgi = 0, BonusInt = 2, BonusWis = 0, BonusVit = 7, BonusLuk = 1,
        BindIndex = 103,                 // Position_Info 103, Alberta 1200 (위치 저장)
        FacingYaw = MathF.PI / 2,        // 90 deg -> 45
    };

    private static byte[] Head(Character c)
    {
        using var w = new PacketWriter(256);
        WorldEntryPackets.WriteCharacterHead205(w, c, 0x0DE0B6B3A7640101UL, 0x0011A6E2u, 1200u, 728f, 537f, 57f, 0);
        return w.ToArray();
    }

    [Fact]
    public void Head2310_CarriesBindIndex_Facing_AndZerosTo111()
    {
        var head = Head(Hero());
        Assert.Equal(205, head.Length);
        Assert.Equal((ushort)103, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(105)));
        Assert.Equal((byte)45, head[107]);
        Assert.Equal("00000000", Hex(head[108..112]));          // always zero
        Assert.Equal((ulong)0, BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(112)));   // wallet untouched (Gold 0)
    }

    [Fact]
    public void Head2310_NoBindNoFacing_KeepsTheOldZeros()
    {
        var c = Hero();
        c.BindIndex = 0;
        c.FacingYaw = 0f;
        Assert.Equal("00000000000000", Hex(Head(c)[105..112]));
    }

    [Fact]
    public void Head2310_BonusBlock_EqualsThe2498Fields()
    {
        // The bonus-stat block mirrors the 2498 trait blob: head[151..160] == blob+26..34, head[161..162] == blob+36,
        // head[163..164] == blob+38.
        var c = Hero();
        var head = Head(c);
        var sheet = TraitPackets.BuildFullPcTrait2498(c, null, null, 100, 10);
        var blob = sheet.AsSpan(3);
        Assert.Equal(Hex(blob[26..40].ToArray()), Hex(head[151..165]));

        // Level 30: 23 points to level 24, 2 per level after = 35 earned; 4 + 2 + 7 + 1 (LUK counts) spent = 21.
        //            STR   AGI   INT   WIS   VIT   [161] unspent 21
        Assert.Equal("0400" + "0000" + "0200" + "0000" + "0700" + "0000" + "1500", Hex(head[151..165]));
        Assert.Equal("ffffffffffffffff", Hex(head[165..173]));    // the sentinels still follow
    }

    [Fact]
    public void Head2310_UnspentNeverNegative()
    {
        var c = Hero();
        c.Level = 2;          // 1 point earned, 14 spent
        var head = Head(c);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(163)));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(159)));   // VIT still reported
    }

    [Fact]
    public void Head2487_StampsTheLiveBindAndFacing_OverAStaleSnapshot()
    {
        var stale = Hero();                       // the DB row: bind 103, facing 45
        var player = new PlayerState
        {
            SessionId = 61, EntityId = 0x0011A6E2u, CharacterUid = 0x0DE0B6B3A7640101UL, Level = 30, MapId = 1221,
            X = 454f, Y = 337f, Z = 8.8f, Hp = 900, Mp = 90, BindIndex = 410, FacingYaw = MathF.PI,
        };
        var head = PcSaveDataPackets.BuildLiveHead205(player, stale, 3);
        Assert.Equal((ushort)410, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(105)));
        Assert.Equal((byte)90, head[107]);
    }

    [Fact]
    public void Head2391_CarriesTheVictimsLiveBindAndFacing()
    {
        var victim = new PlayerState
        {
            SessionId = 62, EntityId = 0x0011A7E2u, CharacterUid = 0x0DE0B6B3A7640102UL, Level = 30, MapId = 1200,
            BindIndex = 360, FacingYaw = -MathF.PI / 2, Character = Hero(),
        };
        var body = PlayerDeathPackets.BuildOnDeadMe2391(victim, Array.Empty<byte[]>(), 0);
        var head = body.AsSpan(8, 205);            // [u32 innerLen][u32 handle][head]
        Assert.Equal((ushort)360, BinaryPrimitives.ReadUInt16LittleEndian(head[105..]));
        Assert.Equal((byte)135, head[107]);
        Assert.Equal((ushort)21, BinaryPrimitives.ReadUInt16LittleEndian(head[163..]));
    }

    // ---- state feeding the head ---------------------------------------------------------------------------------

    [Fact]
    public void Move1526_TracksFacing_IntoStateAndSnapshot()
    {
        var player = new PlayerState { Character = new Character() };
        var payload = new byte[97];
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(68), -2.5f);
        ViewGroupMoveHandler.TrackFacing(player, payload);
        Assert.Equal(-2.5f, player.FacingYaw);
        Assert.Equal(-2.5f, player.Character!.FacingYaw);

        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(68), float.NaN);
        ViewGroupMoveHandler.TrackFacing(player, payload);          // not finite: kept
        Assert.Equal(-2.5f, player.FacingYaw);
        ViewGroupMoveHandler.TrackFacing(player, new byte[60]);     // too short: kept
        Assert.Equal(-2.5f, player.FacingYaw);
    }

    [Fact]
    public void FacingYaw_IsPersistent()
    {
        var sink = new CountingSink();
        var player = new PlayerState { CharacterId = 7 };
        player.PersistSink = sink;
        player.FacingYaw = 1.25f;
        player.FacingYaw = 1.25f;      // unchanged: no second mark
        Assert.Equal(1, sink.Marks);
    }

    // ---- 2498 blob+68: 1 + the weapon's Auto_Attack_Range ------------------------------------------------------------

    [Theory]
    [InlineData(0, 1.0f)]    // unarmed: 1.0
    [InlineData(2, 3.0f)]    // melee (TraitInfo 42 = 2.0): 3.0
    [InlineData(20, 21.0f)]  // bow (20.0): 21.0
    public void PcTrait2498_ReachIsOnePlusWeaponRange(int range, float expected)
    {
        var template = Convert.FromHexString(Retail2498);
        var flat = new Dictionary<byte, int>();
        if (range > 0) flat[42] = range;
        var equip = new EquipStats(flat, new Dictionary<byte, float>());
        var body = TraitPackets.BuildPcTrait2498FromDonor(template, Hero(), equip);
        Assert.Equal(expected, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(3 + 68)));
    }

    [Fact]
    public void PcTrait2498_NoGearInformation_KeepsTheTemplateReach()
    {
        var template = Convert.FromHexString(Retail2498);
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(71)));
        var body = TraitPackets.BuildPcTrait2498FromDonor(template, Hero(), equip: null);
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(71)));
    }

    // ---- 1677 / glider: the warp burst carries no 2497 and no 2435 ----------------------------------------------------

    [Fact]
    public async Task WarpBurst_SendsNo2497AndNo2435_ALoginStillDoes()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var c = Hero();
        c.Gold = 34_913;

        await WorldEntryService.SendWorldEntryAsync(rig.Session, c, 0x0DE0B6B3A7640101UL, 0x0011A6E2u,
            NullLogger.Instance, gameData: data, isWarp: true, warpSpawn: (1100u, 739f, 767f, 56f));
        var warp = await rig.FramesUntilMarkerAsync();
        Assert.Contains(warp, f => f.Op == ZoneS2COpcodes.OnReceiveMapLoad);        // the burst did go out
        Assert.DoesNotContain(warp, f => f.Op == ZoneS2COpcodes.OnChangeActorMoveSpeed);
        Assert.DoesNotContain(warp, f => f.Op == ZoneS2COpcodes.OnZenyChange);

        await WorldEntryService.SendWorldEntryAsync(rig.Session, c, 0x0DE0B6B3A7640101UL, 0x0011A6E2u,
            NullLogger.Instance, gameData: data);
        var login = await rig.FramesUntilMarkerAsync();
        Assert.Contains(login, f => f.Op == ZoneS2COpcodes.OnChangeActorMoveSpeed);
        Assert.Contains(login, f => f.Op == ZoneS2COpcodes.OnZenyChange);
    }

    // ---- a lethal DoT tick on a player ---------------------------------------------------------------------------

    [Fact]
    public async Task LethalDotTick_RunsTheDeathSequence_KillerIsTheCaster()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer(hp: 40);
        var npc = rig.AddMonster();
        rig.Npcs.ApplyDamage(npc.EntityOid, 5, player.EntityId, player.Name);     // the monster holds the victim
        var dot = new ActiveEffect
        {
            EffectId = 4108, SourceSkillId = 0, CasterHandle = npc.EntityOid, TickIntervalMs = 1000, HpPerTick = -92,
        };

        await rig.TickAsync(player, dot);
        var frames = await rig.FramesUntilMarkerAsync();

        Assert.True(player.IsDead);
        Assert.Equal(0, player.Hp);
        Assert.Equal(0, player.Mp);

        // Death order: s6 2398, the lethal 2495 (HP 0 / SP 0), 2391, 2836, 2837, s6 2398, 2392 (killer = the caster,
        // blob+16 = the tick's 92), then the monster's leave-combat 2398.
        var ops = frames.Select(f => f.Op).ToList();
        int s6 = ops.IndexOf(ZoneS2COpcodes.OnChangeState);
        int tick = ops.IndexOf(ZoneS2COpcodes.OnDotDamageNotify);
        int deadMe = ops.IndexOf(ZoneS2COpcodes.OnDeadMe);
        int notify = ops.IndexOf(ZoneS2COpcodes.OnDeadNotify);
        Assert.True(s6 >= 0 && s6 < tick && tick < deadMe && deadMe < notify, string.Join(",", ops));
        Assert.Equal(ZoneS2COpcodes.NfySpinelRevivalInfo, ops[deadMe + 1]);
        Assert.Equal(ZoneS2COpcodes.NfyPCCafeRevivalInfo, ops[deadMe + 2]);
        Assert.Equal(ZoneS2COpcodes.OnChangeState, ops[deadMe + 3]);
        Assert.Equal(Hex(PlayerDeathPackets.BuildOnChangeStateDead2398(player.EntityId)), frames[s6].Body);

        Assert.Equal(Hex(SkillDamagePackets.BuildDotDamageNotify2495(player.EntityId, npc.EntityOid, 0, 92, 0, 0)),
            frames[tick].Body);
        Assert.Equal(Hex(ActorVitalsPackets.BuildDeadNotify2392(player.EntityId, npc.EntityOid, 92)), frames[notify].Body);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.OnDotDamageNotify);
        Assert.DoesNotContain(frames, f => f.Op == ZoneS2COpcodes.OnChangeAbnormalSP);
        // The 2391 head: the victim's live bind (Position_Info 13, Prontera centre) and HP / SP 0.
        var deadHead = Convert.FromHexString(frames[deadMe].Body).AsSpan(8, 205);
        Assert.Equal((ushort)13, BinaryPrimitives.ReadUInt16LittleEndian(deadHead[105..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(deadHead[81..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(deadHead[85..]));

        // The monster is out of the fight: its state-1 2398 after the 2392.
        Assert.Contains(frames.Skip(notify + 1), f => f.Op == ZoneS2COpcodes.OnChangeState
            && f.Body == Hex(CombatPackets.BuildChangeState2398(npc.EntityOid, CombatPackets.ActorState.NpcLeaveCombat, trailing: 3)));
        lock (npc.ThreatLock)
            Assert.False(npc.Threat.ContainsKey(player.EntityId));
    }

    [Fact]
    public async Task LethalDotTick_OnACorpse_OrWithoutADeathRunner_DoesNotKill()
    {
        var data = await Data.Value;
        // No NpcCombatService in the container: the old floor at 1 HP stays, and no 2391 goes out.
        await using var rig = await Rig.StartAsync(data, withCombat: false);
        var player = rig.AddPlayer(hp: 40);
        var dot = new ActiveEffect { EffectId = 4108, CasterHandle = player.EntityId, TickIntervalMs = 1000, HpPerTick = -92 };
        await rig.TickAsync(player, dot);
        var frames = await rig.FramesUntilMarkerAsync();
        Assert.Equal(1, player.Hp);
        Assert.False(player.IsDead);
        Assert.DoesNotContain(frames, f => f.Op == ZoneS2COpcodes.OnDeadMe);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.OnDotDamageNotify);
    }

    [Fact]
    public async Task NonLethalDotTick_OnlyLowersHp()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data);
        var player = rig.AddPlayer(hp: 500);
        var dot = new ActiveEffect { EffectId = 4108, CasterHandle = player.EntityId, TickIntervalMs = 1000, HpPerTick = -92 };
        await rig.TickAsync(player, dot);
        var frames = await rig.FramesUntilMarkerAsync();
        Assert.Equal(408, player.Hp);
        Assert.False(player.IsDead);
        Assert.Equal(new[] { ZoneS2COpcodes.OnDotDamageNotify }, frames.Select(f => f.Op).ToArray());
    }

    // ---- shared ---------------------------------------------------------------------------------------------------

    private sealed class CountingSink : IDirtySink
    {
        public int Marks;
        public void Mark(PlayerState player) => Marks++;
        public void Forget(uint characterId) { }
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

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
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")) && File.Exists(Path.Combine(candidate, "Position_Info.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
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
    /// singletons the DoT tick and the death resolve (GameDataStore, BroadcastService, EffectService, NpcStateManager,
    /// DurabilityService, NpcChatService, NpcCombatService and an empty inventory) and no database.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly Socket _client;
        private readonly ServiceProvider _services;

        public ProudSession Session { get; }
        public PlayerStateManager Players { get; }
        public NpcStateManager Npcs { get; }
        public EffectService Effects { get; }

        private Rig(ProudServer server, Socket client, ProudSession session, GameDataStore data, bool withCombat)
        {
            _server = server;
            _client = client;
            Session = session;
            Players = new PlayerStateManager(new NoDirtySink());
            Npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var players = Players;
            var npcs = Npcs;
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
            if (withCombat)
                collection.AddSingleton(sp => new NpcCombatService(npcs, players, broadcast,
                    sp.GetRequiredService<DurabilityService>(), sp.GetRequiredService<NpcChatService>(),
                    new PcCombatStateService(players, broadcast, NullLogger<PcCombatStateService>.Instance),
                    sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<NpcCombatService>.Instance));
            _services = collection.BuildServiceProvider();
            Effects = _services.GetRequiredService<EffectService>();
        }

        public static async Task<Rig> StartAsync(GameDataStore data, bool withCombat = true)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Rig(server, client, session, data, withCombat);
        }

        /// <summary>An in-world PC on map 1100, alone there, registered for the broadcasts.</summary>
        public PlayerState AddPlayer(int hp)
        {
            var player = new PlayerState
            {
                SessionId = Session.SessionId, EntityId = 0x0D790911, CharacterId = 915_611, Name = "Dotted", Level = 30,
                MapId = 1100, MapLoaded = true, Hp = hp, MaxHp = 5000, Mp = 300, MaxMp = 500, BindIndex = 13,
                Character = Hero(),
            };
            Players.AddPlayer(Session.SessionId, player);
            return player;
        }

        /// <summary>A hostile monster on the PC's map, tracked by the NpcStateManager.</summary>
        public NpcRuntimeState AddMonster()
        {
            var spawn = new SpawnEntry { MapId = 1100, Index = 9_101, AssetIndex = 0, PosX = 500f, PosY = 500f };
            Npcs.TrackSpawns(1100, [spawn]);
            var npc = Npcs.GetAliveNpcs(1100).Single();
            npc.Camp = 2;
            npc.Level = 1;
            npc.MaxHp = 100;
            npc.Hp = 100;
            return npc;
        }

        /// <summary>One periodic tick of <paramref name="effect"/> on <paramref name="player"/> (EffectService's private
        /// TickPlayerEffectAsync, which its timer loop calls), awaited.</summary>
        public Task TickAsync(PlayerState player, ActiveEffect effect)
        {
            var tick = typeof(EffectService).GetMethod("TickPlayerEffectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)tick.Invoke(Effects, new object[] { player, effect })!;
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
