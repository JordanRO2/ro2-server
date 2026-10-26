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
using RO2.Worlds.Domain;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Vitals of the NPC 2506, 2317 and 2319 records and of 2643, the active-effect list of the 2319 record, and the
/// wait before a gatherable corpse leaves.
/// </summary>
public class NpcVitalsReauditTests
{
    
    private const string SmallTable = """
        {"assets":[{"asset":10050004,"level":10,"maxHp":551,"maxSp":693,"n":5,"share":1.0,"sessions":2}],
         "maxSpByLevel":[{"level":50,"value":3843,"n":9,"share":0.9}]}
        """;

    [Fact]
    public void ResolveVitals_UsesRetailsPair_ThenTheLevelsSp_ThenTheOldMirror()
    {
        var manager = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance)
        {
            RetailVitals = NpcRetailVitals.Parse(Encoding.UTF8.GetBytes(SmallTable)),
        };
        
        Assert.Equal((551, 693), manager.ResolveVitals(10050004, 10, 563));
        
        Assert.Equal((32342, 3843), manager.ResolveVitals(50002110, 50, 32342));
        
        Assert.Equal((120000, 65535), manager.ResolveVitals(1, 99, 120000));
        
        var bare = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance) { RetailVitals = null };
        Assert.Equal((563, 563), bare.ResolveVitals(10050004, 10, 563));
    }

    [Fact]
    public void RetailVitalsTable_RejectsDuplicatesAndOutOfRangeSp()
    {
        Assert.Throws<InvalidDataException>(() => NpcRetailVitals.Parse(Encoding.UTF8.GetBytes(
            """{"assets":[{"asset":1,"level":1,"maxHp":5,"maxSp":21},{"asset":1,"level":1,"maxHp":6,"maxSp":21}],"maxSpByLevel":[]}""")));
        Assert.Throws<InvalidDataException>(() => NpcRetailVitals.Parse(Encoding.UTF8.GetBytes(
            """{"assets":[{"asset":1,"level":1,"maxHp":5,"maxSp":70000}],"maxSpByLevel":[]}""")));
    }

 /// <summary>Checks the shipped NPC vitals table against the values the audit was based on.</summary>
    [Fact]
    public void ShippedRetailVitals_CarryTheMeasuredPairs()
    {
        var table = NpcRetailVitals.Load(Path.Combine(RequireDataDirectory(), "npc", "NpcRetailVitals.json"));
        Assert.True(table.Count > 3000);                       
        Assert.True(table.TryGet(11004101, 50, out var hp, out var sp));
        Assert.Equal((9407, 3843), (hp, sp));
        Assert.True(table.TryGet(10050004, 10, out hp, out sp));
        Assert.Equal((551, 693), (hp, sp));
        Assert.True(table.TryGet(10050000, 20, out hp, out sp));
        Assert.Equal((1340, 1659), (hp, sp));
        Assert.True(table.TryGetLevelMaxSp(50, out sp));
        Assert.Equal(3843, sp);
        Assert.True(table.TryGetLevelMaxSp(80, out sp));
        Assert.Equal(11445, sp);
    }

 /// <summary>
 /// A standing 2319 for a level 50 NPC. With the NPC's SP set, the whole 93-byte body matches the expected layout.
 /// </summary>
    [Fact]
    public void NpcViewData2319_WithTheMeasuredSp_IsTheArchiveFrameByteForByte()
    {
        const string retail = "015b5b0098e20000000013a74901bef8fa0232b5f40100c8cd0100cb050000b5f40100c8cd0100cb050000" +
                              "01000000000000803f567e0000567e0000030f0000030f000000000000000000000000000000000300000000000000000000";
        var start = new DateTime(2026, 7, 21, 8, 37, 38, 569, DateTimeKind.Utc);
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x0149A713u, NpcId = 50002110u, MapId = 1u, Level = 50,
            Hp = 32342, MaxHp = 32342, MaxSp = 3843, Camp = 2, NavCell = 1483,
            PosX = 128181 / 100f, PosY = 118216 / 100f,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 50002110u, Rot = 0f },
            LastMove = new NpcMove(128165 / 100f, 118203 / 100f, 128181 / 100f, 118216 / 100f, start, 1.0f),
        };

        Assert.Equal(retail, Hex(NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 1.0f, nowUtc: start.AddMilliseconds(364))));
        
        Assert.Equal(3843u, SkillDamagePackets.NpcTargetSp(npc));
    }

 /// <summary>A 2643 for a level 30 NPC at full HP and SP with no target. The icon block restates the NPC's own vitals.</summary>
    [Fact]
    public void ActorIconInfo2643_OfAnNpc_RestatesItsVitals_AsTheArchiveFrame()
    {
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x00001F13u, Level = 30, Hp = 2084, MaxHp = 2084, MaxSp = 2394,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 1u },
        };
        Assert.Equal("131f000024080000240800005a095a091e00000000", Hex(ReqActorDetailInfoHandler.BuildIconOf(npc)));
    }

    [Fact]
    public void RegenNpc2317_CarriesTheMeasuredSp_CurEqualsMax_AndNoExtension()
    {
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x0149A713u, NpcId = 50002110u, MapId = 1u, Level = 50,
            Hp = 32342, MaxHp = 32342, MaxSp = 3843, Camp = 2, NavCell = 1483,
            PosX = 1281.81f, PosY = 1182.16f,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 50002110u },
        };
        var body = NpcLifecyclePackets.BuildRegenNpc2317(npc, walkSpeed: 1.0f);
        Assert.Equal(93, body.Length);                         
        var rec = body.AsSpan(10);
        Assert.Equal(32342u, BinaryPrimitives.ReadUInt32LittleEndian(rec[42..]));
        Assert.Equal(32342u, BinaryPrimitives.ReadUInt32LittleEndian(rec[46..]));
        Assert.Equal(3843, BinaryPrimitives.ReadUInt16LittleEndian(rec[50..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec[52..]));
        Assert.Equal(3843, BinaryPrimitives.ReadUInt16LittleEndian(rec[54..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec[56..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec[81..]));
    }

    
 /// <summary>A 116-byte 2319 whose extension list holds one active effect with its remaining duration and source skill.</summary>
    [Fact]
    public void NpcViewData2319_WithOneEffect_IsRetails116ByteForm()
    {
        var now = new DateTime(2026, 9, 27, 8, 59, 7, 45, DateTimeKind.Utc);
        var npc = StandingNpc(0x00959013u);
        var effects = new[]
        {
            new ActiveEffect { EffectId = 42011, EffectValue = 1, StackCount = 1, SourceSkillId = 420110,
                CasterHandle = 0x0E871311, ExpiresAt = now.AddMilliseconds(9194) },
        };

        var body = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 3.0f, nowUtc: now, effects: effects);

        Assert.Equal(116, body.Length);
        Assert.Equal("0172720098e200000000", Hex(body[..10]));
        Assert.Equal("01001ba40000010001ea230000000000000e6906001113870e", Hex(body[(10 + 81)..]));
    }

 /// <summary>
 /// A 140-byte 2319 with two effects in ascending id behind a 2-byte compact length. Only the HP fields
 /// differ from the fixed layout.
 /// </summary>
    [Fact]
    public void NpcViewData2319_WithTwoEffects_IsRetails140ByteForm()
    {
        const string retail =
            "028900890098e200000000" +
            "13599c0210260f0150b82b0100aa01020093000000b82b0100aa0102009300000001000000009a99993e1145cd007a4dce00" +
            "b52c0000b52c00000000000000000000000001000000ee0300000000000000" +
            "0200de170000010001c60f0000000000001177090011dccb0e3b180000050001c60f0000000000001177090011dccb0e";
        var now = new DateTime(2026, 8, 9, 22, 36, 36, 490, DateTimeKind.Utc);
        var npc = new NpcRuntimeState
        {
            EntityOid = 0x029C5913u, NpcId = 17770000u, MapId = 1u, Level = 80,
            Hp = 13452561, MaxHp = 13520250, MaxSp = 11445, Camp = 1, NavCell = 147,
            PosX = 76728.5f / 100f, PosY = 131498.5f / 100f,
            Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 17770000u, Rot = 0f },
        };
        
        var effects = new[]
        {
            new ActiveEffect { EffectId = 6203, EffectValue = 5, StackCount = 1, SourceSkillId = 620305,
                CasterHandle = 0x0ECBDC11, ExpiresAt = now.AddMilliseconds(4038) },
            new ActiveEffect { EffectId = 6110, EffectValue = 9, StackCount = 1, SourceSkillId = 620305,
                CasterHandle = 0x0ECBDC11, ExpiresAt = now.AddMilliseconds(4038) },
            new ActiveEffect { EffectId = 6110, EffectValue = 1, StackCount = 1, SourceSkillId = 620305,
                CasterHandle = 0x0ECBDC11, ExpiresAt = now.AddMilliseconds(4038) },
            new ActiveEffect { EffectId = 7000, EffectValue = 1, StackCount = 1, ExpiresAt = now.AddSeconds(-1) },
        };

        var body = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 0.3f, nowUtc: now, effects: effects);
        var expected = Convert.FromHexString(retail);

        Assert.Equal(expected.Length, body.Length);
        Assert.Equal(expected[..11], body[..11]);
        Assert.Equal(expected[(11 + 81)..], body[(11 + 81)..]);
        var differing = Enumerable.Range(0, NpcSpawnService.NpcRecordLen).Where(i => body[11 + i] != expected[11 + i]).ToArray();
        Assert.Equal(new[] { 68, 72, 73 }, differing);
    }

    [Fact]
    public void NpcViewData2319_OfADeadNpc_CarriesNoEffects()
    {
        var npc = StandingNpc(0x00959013u);
        npc.IsDead = true;
        var effects = new[] { new ActiveEffect { EffectId = 42011, EffectValue = 1 } };
        var body = NpcSpawnService.BuildOnNpcViewData2319(npc, walkSpeed: 3.0f, nowUtc: DateTime.UtcNow, effects: effects);
        Assert.Equal(93, body.Length);
    }

    private static NpcRuntimeState StandingNpc(uint handle) => new()
    {
        EntityOid = handle, NpcId = 17759999u, MapId = 1u, Level = 80,
        Hp = 345679, MaxHp = 431564, MaxSp = 11445, Camp = 2, NavCell = 1715,
        PosX = 291.31f, PosY = 811.23f,
        Spawn = new SpawnEntry { MapId = 1u, Index = 1, AssetIndex = 17759999u, Rot = 320f },
    };

    
 /// <summary>Config_Def type 30 (corpse gathering wait) is 40 seconds in the shipped data: the extra stay of a gatherable corpse.</summary>
    [Fact]
    public async Task CorpseGatherWait_IsConfigDefType30_FortySeconds()
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit-a0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new GameDataStore();
        try
        {
            File.Copy(Path.Combine(source, "Config_Def.json"), Path.Combine(dir, "Config_Def.json"));
            await store.LoadAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Assert.Equal(TimeSpan.FromSeconds(40), LootService.CorpseGatherWait(store, NullLogger.Instance));
        
        Assert.Equal(TimeSpan.Zero, LootService.CorpseGatherWait(new GameDataStore(), NullLogger.Instance));
    }
}
