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
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for NPC targeting and armour wear on skill casts: the NPC victim set of a skill (SkillDamageHandler.SelectNpcVictims), the damage records of an NPC element (max(1, Number_Of_Normal_Influences)), the armour wear of the PCs an element lists (DurabilityService.SkillWearPcHandles), and the template value of an unset armour row.
/// </summary>
public class Reaudit2_A0_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    
 /// <summary>Every listed PC handle, in element order, once; NPC handles and the zero handle never.</summary>
    [Fact]
    public void SkillWearPcHandles_AreTheListedPcs_InElementOrder_Deduplicated()
    {
        const uint caster = 0x06FC0E11, other = 0x06FC0F11, npc = 0x00112E13;
        var hits = new[]
        {
            new SkillDamageHit(npc, 100, 50, [new SkillHitEffect(1, 10)]),
            new SkillDamageHit(other, 900, 40),
            new SkillDamageHit(caster, 800, 30),
            new SkillDamageHit(other, 900, 40),
            new SkillDamageHit(0, 0, 0),
        };
        Assert.Equal(new uint[] { other, caster }, DurabilityService.SkillWearPcHandles(hits, false, [caster]));
    }

 /// <summary>A cast whose element lists only monsters wears no PC, and an empty vector wears none either.</summary>
    [Fact]
    public void SkillWearPcHandles_NpcOnlyOrEmpty_WearsNobody()
    {
        var npcs = new[] { new SkillDamageHit(0x00112E13, 1, 1, [new SkillHitEffect(1, 5)]) };
        Assert.Empty(DurabilityService.SkillWearPcHandles(npcs, false, [0x00112E13u]));
        Assert.Empty(DurabilityService.SkillWearPcHandles([], false, []));
    }

 /// <summary>A type-10 cast that names one PC gives that PC the single all-zero element, and the named PC's armour wears when it is cast on itself.</summary>
    [Fact]
    public void SkillWearPcHandles_SingleZeroCast_WearsTheNamedPc()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "2f000000f8ab1e0001114def06000000000000000000000000dde8423f79f8253f000000000000000000000000000000000000"));
        Assert.True(SkillDamagePcElements.IsSingleZeroElementCast(SkillDamagePcElements.TypeSelf, request.Targets));
        var hits = new[] { SkillDamagePcElements.SingleZeroElement() };
        Assert.Equal(new uint[] { 0x06EF4D11 }, DurabilityService.SkillWearPcHandles(hits, true, request.Targets));
        
        Assert.Empty(DurabilityService.SkillWearPcHandles(hits, false, request.Targets));
    }

 /// <summary>A buff that the caster casts on itself lists the caster, so the caster's own armour wears.</summary>
    [Fact]
    public void SkillWearPcHandles_SelfBuff_WearsTheCaster()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "2f0000001f5e030001110efc0634993c441c3e3c44014a5c425c557abf324256be000000000202000000000000000000000000"));
        var caster = new PlayerState { EntityId = 0x06FC0E11, MapId = 1100, Hp = 53434, Mp = 11704 };
        var element = SkillDamagePcElements.PcElement(SkillDamagePcElements.TypeBuff, caster, caster);
        Assert.NotNull(element);
        Assert.Equal(new uint[] { 0x06FC0E11 },
            DurabilityService.SkillWearPcHandles([element.Value], false, request.Targets));
    }

 /// <summary>An unset (-1) durability row stands for its template's Durability, including 0. With no template, the 10000 ceiling applies.</summary>
    [Fact]
    public void UnsetDurability_IsTheTemplateValue_ZeroIncluded()
    {
        Assert.Equal(0, DurabilityService.UnsetDurabilityOf(new ItemData { Durability = 0 }));
        Assert.Equal(10000, DurabilityService.UnsetDurabilityOf(new ItemData { Durability = 10000 }));
        Assert.Equal(ushort.MaxValue, DurabilityService.UnsetDurabilityOf(new ItemData { Durability = 70000 }));
        Assert.Equal(DurabilityService.DefaultMaxDurability, DurabilityService.UnsetDurabilityOf(null));
    }

 /// <summary>
 /// The armour burst with two unset rows: a template Durability of 0 is sent as 0 and the row is not rewritten; the next row (Durability 10000) is sent at 9998, persisted, in ascending slot order. The 2503 frame shape is that of ItemPackets.BuildItemDurability2503.
 /// </summary>
    [Fact]
    public async Task ArmourWear_UnsetTemplateZeroPiece_GoesOutAtZero_AndANormalPieceStepsFromItsTemplate()
    {
        var store = await LoadStoreAsync("ItemInfo.json");
        Assert.Equal(0u, store.GetItem(50001129)!.Durability);
        Assert.Equal(1, store.GetItem(50001129)!.Equip_Slot);
        Assert.Equal(2, DurabilityService.ArmourWearStep(store.GetItem(12100001)!));

        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        const uint charId = 91;
        var bag = new GearInventory(
        [
            new InventoryItem { Id = 0x0E27C49888FD3002, CharacterId = charId, ItemId = 12100001, StorageType = 1, Slot = 2, Durability = -1 },
            new InventoryItem { Id = 0x0E27C49888FD3001, CharacterId = charId, ItemId = 50001129, StorageType = 1, Slot = 1, Durability = -1 },
        ]);
        var collection = new ServiceCollection();
        collection.AddScoped<IInventoryRepository>(_ => bag);
        await using var services = collection.BuildServiceProvider();
        var durability = new DurabilityService(store, NullLogger<DurabilityService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), broadcast);
        var victim = new PlayerState { SessionId = net.Sessions[0].SessionId, CharacterId = charId, MapId = 1100 };

        await durability.ApplyArmourWearOnTakenHitAsync(victim);
        var r = net.Readers[0];
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, "01121200cde300000000" + "0130fd8898c4270e" + "0000"),
            Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, "01121200cde300000000" + "0230fd8898c4270e" + "0e27"),
            Hexed(await r.NextAsync()));
        Assert.Equal((short)-1, bag.Items[1].Durability);     
        Assert.Equal((short)9998, bag.Items[0].Durability);   
    }

    
 /// <summary>The Skill_Table_SkillLevel columns as the handler reads them: 0 counts as 1, and an unknown skill gets one record with no quantity limit.</summary>
    [Fact]
    public void SkillLevelColumns_ReadTheShippedTable()
    {
        var rows = SkillLevelColumns.Parse(File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_SkillLevel.json")));
        Assert.Equal(6551, rows.Count);
        Assert.Equal(2, SkillLevelColumns.NormalInfluencesOf(rows, 410101));
        Assert.Equal(3, SkillLevelColumns.NormalInfluencesOf(rows, 120210));
        Assert.Equal(5, SkillLevelColumns.NormalInfluencesOf(rows, 521010));
        Assert.Equal(1, SkillLevelColumns.NormalInfluencesOf(rows, 111001));
        Assert.Equal(0, rows[114401].NormalInfluences);
        Assert.Equal(1, SkillLevelColumns.NormalInfluencesOf(rows, 114401));   
        Assert.Equal(1, SkillLevelColumns.NormalInfluencesOf(rows, 999999));   
        Assert.Equal(5, SkillLevelColumns.DamageQuantityOf(rows, 620305));
        Assert.Equal(4, SkillLevelColumns.DamageQuantityOf(rows, 510203));
        Assert.Equal(0, rows[410601].DamageQuantity);
        Assert.Equal(1, SkillLevelColumns.DamageQuantityOf(rows, 410601));     
        Assert.Null(SkillLevelColumns.DamageQuantityOf(rows, 999999));
    }

 /// <summary>
 /// The records of one NPC element share the skill's DamageRate. Each carries the full hit divided by N, so together they carry it once. Never below 1, as in CombatService.
 /// </summary>
    [Theory]
    [InlineData(9000, 1, 9000)]
    [InlineData(9000, 3, 3000)]
    [InlineData(7811, 2, 3906)]   
    [InlineData(47184, 5, 9437)]
    [InlineData(1, 5, 1)]
    [InlineData(500, 0, 500)]     
    public void RecordDamage_IsTheFullHitOverTheRecordCount(int full, int records, int expected)
        => Assert.Equal(expected, SkillLevelColumns.RecordDamage(full, records));

 /// <summary>Whatever the count, the records of one element add up to the full hit within rounding, never N times it.</summary>
    [Fact]
    public void RecordDamage_RecordsSumToTheFullHit()
    {
        foreach (var full in new[] { 10, 999, 3905, 23592, 100000 })
            for (var n = 1; n <= 5; n++)
                Assert.InRange(SkillLevelColumns.RecordDamage(full, n) * n, full - n, full + n);
    }

 /// <summary>
 /// Influence_Target_Type 9 (the two Detection skills) has NPC elements with no damage record. Every other type-1 NPC element has at least one. An ordinary attack skill is type 1.
 /// </summary>
    [Fact]
    public void InfluenceTargetTypes_DetectionIsNine_AttackSkillsAreNot()
    {
        var types = SkillLevelColumns.ParseInfluenceTargetTypes(
            File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_Base.json")));
        Assert.Equal(SkillLevelColumns.DetectionInfluenceTargetType, types[6321]);
        Assert.Equal(SkillLevelColumns.DetectionInfluenceTargetType, types[60203]);
        Assert.Equal(new uint[] { 6321, 60203 },
            types.Where(kv => kv.Value == SkillLevelColumns.DetectionInfluenceTargetType).Select(kv => kv.Key).Order().ToArray());
        Assert.Equal(1, types[1202]);
        Assert.Equal(1, types[4101]);
    }

    
 /// <summary>
 /// A cast aimed at an NPC primary lists the primary alone, not the whole hit list.
 /// </summary>
    [Fact]
    public void NpcVictims_PrimaryNpc_IsThePrimaryAlone_AndTheElementIsTheArchiveFrame()
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(
            "3b000000117709000113c6420200000000000000000000000000000080000080bf00000080041400000000000000010100030013f80f0213f90f0213c64202"));
        Assert.Equal(new uint[] { 0x0242C613, 0x020FF813, 0x020FF913 }, request.Targets);
        var npcs = LiveNpcs(0x0242C613, 0x020FF813, 0x020FF913);
        Assert.Equal(new uint[] { 0x0242C613 },
            SkillDamageHandler.SelectNpcVictims(request, 0x0789A111, npcs.Get, 5));

        var body = SkillDamagePackets.BuildSkillDamage2718(0x0789A111, request.SkillId, request.PrimaryTarget,
            [new SkillDamageHit(0x0242C613, 0x00D027D3, 0x2CB5, [new SkillHitEffect(1, 0xD6E8)])],
            request.X, request.Y, request.Z, request.CastType, request.SkillEffectIndex, request.Unknown34);
        Assert.Equal("015050004be4000000001177090011a189070113c642020000000000000000000000000414000000000000000100000113c64202"
            + "d327d000b52c00000000000000000000000000000101e8d6000000000000", Hex(body));
    }

 /// <summary>A primary that is another PC damages no NPC; the caster as primary is an area cast over the hit list.</summary>
    [Fact]
    public void NpcVictims_PrimaryOtherPc_IsNone_AndTheCasterAsPrimaryIsTheHitList()
    {
        const uint caster = 0x06FC0E11;
        var npcs = LiveNpcs(0x00112E13, 0x00112F13);
        var atOther = Request(1, 0x06FC0F11, 0x06FC0F11, 0x00112E13, 0x00112F13);
        Assert.Empty(SkillDamageHandler.SelectNpcVictims(atOther, caster, npcs.Get, 10));
        var atSelf = Request(1, caster, caster, 0x00112E13, 0x00112F13);
        Assert.Equal(new uint[] { 0x00112E13, 0x00112F13 }, SkillDamageHandler.SelectNpcVictims(atSelf, caster, npcs.Get, 10));
    }

 /// <summary>
 /// A ground cast keeps the hit list in order, minus dead NPCs and anything past Skill_Damage_Quantity. A friendly NPC (Camp 1) is left out and does not count. An unknown skill is capped at 20.
 /// </summary>
    [Fact]
    public void NpcVictims_GroundCast_SkipsTheDeadAndFriendly_AndStopsAtTheQuantity()
    {
        var npcs = LiveNpcs(0x00000113, 0x00000213, 0x00000313, 0x00000413, 0x00000513);
        npcs.Map[0x00000113].IsDead = true;
        npcs.Map[0x00000213].Camp = 1;
        var ground = Request(0, 0, 0x00000113, 0x00000213, 0x00000313, 0x00000413, 0x00000513, 0x06FC0F11);
        Assert.Equal(new uint[] { 0x00000313, 0x00000413 }, SkillDamageHandler.SelectNpcVictims(ground, 0x06FC0E11, npcs.Get, 2));
        Assert.Equal(new uint[] { 0x00000313, 0x00000413, 0x00000513 },
            SkillDamageHandler.SelectNpcVictims(ground, 0x06FC0E11, npcs.Get, null));

        var many = Request(0, 0, Enumerable.Range(1, 25).Select(i => (uint)(i << 8) | 0x13).ToArray());
        var crowd = LiveNpcs(many.Targets);
        Assert.Equal(SkillDamagePackets.MaxSkillDamageTargets,
            SkillDamageHandler.SelectNpcVictims(many, 0x06FC0E11, crowd.Get, null).Count);
    }

 /// <summary>
 /// The 2718 NPC element carries max(1, Number_Of_Normal_Influences) records, each rolled on its own, so a crit can land on any one of them. Frames with 2, 3 and 5 records are checked byte for byte.
 /// </summary>
    [Theory]
    [InlineData("2f000000f54106000113932f00000000000000000000000000e88668bf8a2bd63e000000000000000000000000000001000000",
        0x070F5211u, 0x0001CE77u, 0x32F1u, new[] { 4, 7811, 1, 3905 },
        "015959004be400000000f541060011520f070113932f000000000000000000000000000000000000000000000100000113932f00"
        + "77ce0100f13200000000000000000000000000000204831e00000000000001410f000000000000")]
    [InlineData("2f00000092d501000113ad103b000000000000000000000000f75e73bffad39e3e000000000000000000000000000001000000",
        0x07EBC711u, 0x00044248u, 0x0F03u, new[] { 1, 7340, 1, 7340, 4, 27645 },
        "016262004be40000000092d5010011c7eb070113ad103b0000000000000000000000000000000000000000000100000113ad103b"
        + "48420400030f00000000000000000000000000000301ac1c00000000000001ac1c00000000000004fd6b000000000000")]
    [InlineData("2f00000032f307000113cf420200000000000000000000000000000080000080bf000000800000000000000000000001000000",
        0x0788A811u, 0x00BC67AEu, 0x2CB5u, new[] { 1, 23591, 1, 23591, 1, 23591, 4, 47184, 1, 23591 },
        "017474004be40000000032f3070011a888070113cf42020000000000000000000000000000000000000000000100000113cf4202"
        + "ae67bc00b52c00000000000000000000000000000501275c00000000000001275c00000000000001275c0000000000000450b8"
        + "00000000000001275c000000000000")]
    public void NpcElement_CarriesNormalInfluencesRecords_IsTheArchiveFrame(string requestHex, uint caster, uint hp,
        uint sp, int[] kindDamage, string retail2718)
    {
        var request = SkillDamageHandler.ParseRequest(Convert.FromHexString(requestHex));
        var rows = SkillLevelColumns.Parse(File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_SkillLevel.json")));
        var records = SkillLevelColumns.NormalInfluencesOf(rows, request.SkillId);
        Assert.Equal(kindDamage.Length / 2, records);

        var npcs = LiveNpcs(request.PrimaryTarget);
        Assert.Equal(new[] { request.PrimaryTarget }, SkillDamageHandler.SelectNpcVictims(request, caster, npcs.Get, 1));

        var effects = Enumerable.Range(0, records)
            .Select(i => new SkillHitEffect((byte)kindDamage[2 * i], kindDamage[2 * i + 1])).ToArray();
        var body = SkillDamagePackets.BuildSkillDamage2718(caster, request.SkillId, request.PrimaryTarget,
            [new SkillDamageHit(request.PrimaryTarget, hp, sp, effects)],
            request.X, request.Y, request.Z, request.CastType, request.SkillEffectIndex, request.Unknown34);
        Assert.Equal(retail2718, Hex(body));
    }

    
    private static SkillDamageHandler.SkillDamageRequest Request(byte hasTarget, uint primary, params uint[] targets)
        => new(1, hasTarget, primary, 0f, 0f, 0f, 0, 0, 0, targets);

    private sealed class NpcLookup(Dictionary<uint, NpcRuntimeState> map)
    {
        public Dictionary<uint, NpcRuntimeState> Map { get; } = map;
        public NpcRuntimeState? Get(uint handle) => Map.GetValueOrDefault(handle);
    }

 /// <summary>Live hostile (Camp 2) NPCs under the given handles.</summary>
    private static NpcLookup LiveNpcs(params uint[] handles)
        => new(handles.Where(h => (h & 0xFF) == 0x13).Distinct()
            .ToDictionary(h => h, h => new NpcRuntimeState { EntityOid = h, Camp = 2, Hp = 100, MaxHp = 100 }));

    private static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Skill_Table_SkillLevel.json")) && File.Exists(Path.Combine(candidate, "ItemInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_SkillLevel.json");
    }

 /// <summary>A GameDataStore holding only the named shipped tables (copied to a temp folder).</summary>
    private static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit2-a0-" + Guid.NewGuid().ToString("N"));
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

 /// <summary>The worn gear an armour-wear test reads and writes (rows are mutated in place, as the service does).</summary>
    private sealed class GearInventory(InventoryItem[] items) : IInventoryRepository
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
    private sealed class Harness : IAsyncDisposable
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

 /// <summary>Reads the RMI frames (<c>[13 57][w][len][01][u16 op][body]</c>) a test client receives.</summary>
    private sealed class FrameReader(Socket socket)
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
