using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the 2630 threat amount (ThreatAmount, NpcStateManager.ApplyDamage) and for the destinations, kinds and landing grant of the warp items (ItemWarpSkills). The 2507 unlooted-lifetime tests are in CorpseLootTests, and the quest-gated Khara tests are in KharaQuestGatedAutoAcceptTests.
/// </summary>
public class Reaudit4_A0_Tests
{
    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    
 /// <summary>
 /// The step rule: (damage + AddAggro) multiplied by (1 + stance / 10000), truncated. A 2696 steps by the damage; a 2797 tick and a 2718 step by damage + 1, each multiplied by the stance rate where one applies. These tests pin the formula only. The stance rate is not modelled in this server, since no path applies a trait-49 effect to a player, so the live call always passes 0.
 /// </summary>
    [Theory]
    [InlineData(185900L, 0, 0d, 185900L)]           
    [InlineData(185900L, 0, 30000d, 743600L)]       
    [InlineData(18439L, 1, 0d, 18440L)]             
    [InlineData(18438L, 1, 35000d, 82975L)]         
    [InlineData(0L, 1, 0d, 1L)]                     
    [InlineData(0L, 0, 0d, 0L)]                     
    [InlineData(-5L, 0, 0d, 0L)]
    [InlineData(100L, 1, -10000d, 0L)]
    public void Step_IsDamagePlusAddAggro_TimesTheStanceRate(long damage, int addAggro, double stance, long expected)
        => Assert.Equal(expected, ThreatAmount.Compute(damage, addAggro, stance));

 /// <summary>AddAggro from the shipped Skill_Table_SkillLevel: 1 on most rows, 10 on four monster skills, 0 on 17. A level the table does not know reads 1.</summary>
    [Fact]
    public void AddAggro_IsReadByLevelId_FromTheShippedTable()
    {
        var rows = ThreatAmount.Parse(File.ReadAllBytes(Path.Combine(DataDir(), "Skill_Table_SkillLevel.json")));
        Assert.Equal(6551, rows.Count);
        Assert.Equal(6530, rows.Values.Count(v => v == 1));
        Assert.Equal(1, ThreatAmount.AddAggroOf(rows, 120201));   
        Assert.Equal(10, ThreatAmount.AddAggroOf(rows, 7047001));
        Assert.Equal(0, ThreatAmount.AddAggroOf(rows, 7510901));
        Assert.Equal(ThreatAmount.DefaultAddAggro, ThreatAmount.AddAggroOf(rows, 99999999));
    }

 /// <summary>The x4 of the knights' steps comes from Aura Armor's trait-49 line at level 5 (Level_Multiply 6000 x 5 = +30,000), and the pet effect 210032 adds Basic 5000.</summary>
    [Fact]
    public void StanceRows_AreTheEffectTableTrait49Lines()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DataDir(), "Effect_Table.json")));
        var rows = doc.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("Task_Type").GetInt32() == (int)ThreatAmount.StanceAggroRateTraitId
                        && r.GetProperty("Trait_Kind").GetInt32() == 4)
            .ToDictionary(r => r.GetProperty("ID").GetUInt32(),
                r => (Basic: r.GetProperty("Basic_Add_Value").GetDouble(), Level: r.GetProperty("Level_Multiply_Value").GetDouble()));
        Assert.Equal((0d, 6000d), rows[1104]);
        Assert.Equal((5000d, 0d), rows[210032]);
        Assert.Equal(4 * 18440L, ThreatAmount.Compute(18439, 1, rows[1104].Basic + rows[1104].Level * 5));
        Assert.Equal(82980L, ThreatAmount.Compute(18439, 1, rows[1104].Level * 5 + rows[210032].Basic));
    }

 /// <summary>
 /// Pins the unmodelled stance rate: the only data-driven ways a player gets an effect here (item Effect_ID_1 and the ranker statue buff) name no trait-49 effect, so ThreatAmount.StanceRateOf is always 0. If this fails, a stance effect has become reachable, and the notes on the stance rate in ThreatAmount and the three damage handlers must be revised.
 /// </summary>
    [Fact]
    public void NoItemOrStatueBuff_AppliesAStanceEffect()
    {
        using var effects = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DataDir(), "Effect_Table.json")));
        var stance = effects.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("Task_Type").GetInt32() == (int)ThreatAmount.StanceAggroRateTraitId
                        && r.GetProperty("Trait_Kind").GetInt32() == 4)
            .Select(r => r.GetProperty("ID").GetUInt32())
            .ToHashSet();
        Assert.Equal([1104u, 3313u, 5314u, 210031u, 210032u], stance.Order());

        using var items = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DataDir(), "ItemInfo.json")));
        Assert.DoesNotContain(items.RootElement.EnumerateArray(),
            r => r.TryGetProperty("Effect_ID_1", out var e) && e.TryGetUInt32(out var id) && stance.Contains(id));

        using var npcs = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DataDir(), "NPCInfo.json")));
        foreach (var npc in npcs.RootElement.EnumerateArray())
            for (var slot = 1; slot <= 3; slot++)
                if (npc.TryGetProperty($"Function_Type_{slot}", out var t) && t.TryGetInt32(out var type) && type == 47
                    && npc.TryGetProperty($"Function_ID_{slot}", out var f) && f.TryGetUInt32(out var buff))
                    Assert.DoesNotContain(buff, stance);
    }

 /// <summary>ApplyDamage banks the amount the hit path names instead of the damage, and a skill's AddAggro even when the hit took no HP. With no amount named it banks one point per point of damage, as the effect DoT path does.</summary>
    [Fact]
    public void ApplyDamage_BanksTheNamedThreat()
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var spawn = new SpawnEntry { MapId = 1300, Index = 384, PosX = 447.5f, PosY = 321.1f, PosZ = 12.5f, AssetIndex = 28580000 };
        npcs.TrackSpawns(spawn.MapId, [spawn]);
        var handle = npcs.AllocateHandle(spawn.MapId, spawn.Index);
        var npc = npcs.GetNpc(handle)!;
        npc.Camp = 2;
        npc.MaxHp = 1000;
        npc.Hp = 1000;
        const uint pc = 118442769;

        npcs.ApplyDamage(handle, 10, pc, "Carl3ee", threat: ThreatAmount.Compute(10, 1, 30000));   
        npcs.ApplyDamage(handle, 0, pc, "Carl3ee", threat: ThreatAmount.Compute(0, 1, 0));         
        npcs.ApplyDamage(handle, 5, pc, "Carl3ee");                                               
        npcs.ApplyDamage(handle, 0, pc, "Carl3ee", threat: 0);                                    
        var steps = new List<uint>();
        while (npcs.TryDequeueAggroChange(out var ch)) steps.Add(ch.Threat);

        Assert.Equal(new uint[] { 44, 45, 50 }, steps.ToArray());
        Assert.Equal(985, npcs.GetNpc(handle)!.Hp);
    }

    
    [Theory]
    [InlineData(9100101u, (int)ItemWarpSkills.MoveKind.OnAnswer)]
    [InlineData(9110001u, (int)ItemWarpSkills.MoveKind.OnAnswer)]
    [InlineData(9100201u, (int)ItemWarpSkills.MoveKind.OnLanding)]
    [InlineData(9100401u, (int)ItemWarpSkills.MoveKind.None)]   
    public void WarpSkillKinds(uint skillId, int kind) => Assert.Equal((ItemWarpSkills.MoveKind)kind, ItemWarpSkills.KindOf(skillId));

 /// <summary>
 /// The town warps' fixed destinations: the positions of the 2311 and 2556 frames, as float bytes, for 9100201 (map 1100), 9100301 (map 1200) and 9100701 (map 1300), and Position_Info 294 and 410 for 9100801 and 9101001.
 /// </summary>
    [Theory]
    [InlineData(9100201u, 1100u, 0x444787AE, 0x443FC666, 0x4264B852)]
    [InlineData(9100301u, 1200u, 0x4434B0A4, 0x4408C1EC, 0x425D47AE)]
    [InlineData(9100701u, 1300u, 0x43D90000, 0x43790000, 0x41400000)]
    public void TownWarp_GoesToTheCapturedPoint(uint skillId, uint map, int xBits, int yBits, int zBits)
    {
        var dest = ItemWarpSkills.TownWarpDestination(skillId, Maps().GetValueOrDefault, Positions())!.Value;
        Assert.Equal(map, dest.MapId);
        Assert.Equal((xBits, yBits, zBits),
            (BitConverter.SingleToInt32Bits(dest.X), BitConverter.SingleToInt32Bits(dest.Y), BitConverter.SingleToInt32Bits(dest.Z)));
    }

    [Fact]
    public void TownWarp_TrainingGroundAndJawaii_AreTheirPositionInfoRows()
    {
        var maps = Maps();
        var positions = Positions();
        Assert.Equal(new ItemWarpSkills.Destination(1111, 1112f, 889f, 85f),
            ItemWarpSkills.TownWarpDestination(ItemWarpSkills.TrainingGroundWarp, maps.GetValueOrDefault, positions));
        Assert.Equal(new ItemWarpSkills.Destination(1221, 454f, 337f, positions[410].Position_Z),
            ItemWarpSkills.TownWarpDestination(ItemWarpSkills.JawaiiWarp, maps.GetValueOrDefault, positions));
        
        Assert.Null(ItemWarpSkills.TownWarpDestination(ItemWarpSkills.PronteraWarp, _ => null, positions));
        Assert.Null(ItemWarpSkills.TownWarpDestination(9100401, maps.GetValueOrDefault, positions));
    }

 /// <summary>The Dungeon Wing goes to the current map's Map_List return point. It is refused on a Map_Type 0 map, including map 1221, whose Map_ID is its own id.</summary>
    [Fact]
    public void DungeonWing_GoesToTheReturnPoint_AndIsRefusedOnAFieldMap()
    {
        var maps = Maps();
        Assert.Equal(new ItemWarpSkills.Destination(1101, 1538f, 664f, 12f),
            ItemWarpSkills.DungeonWingDestination(maps[410103], maps.GetValueOrDefault));
        Assert.Equal(new ItemWarpSkills.Destination(1300, 78f, 468f, 44f),
            ItemWarpSkills.DungeonWingDestination(maps[4301], maps.GetValueOrDefault));
        Assert.Equal(new ItemWarpSkills.Destination(1100, 725f, 796f, 55.5f),
            ItemWarpSkills.DungeonWingDestination(maps[5101], maps.GetValueOrDefault));
        Assert.Null(ItemWarpSkills.DungeonWingDestination(maps[1100], maps.GetValueOrDefault));
        Assert.Null(ItemWarpSkills.DungeonWingDestination(maps[1221], maps.GetValueOrDefault));
        Assert.Null(ItemWarpSkills.DungeonWingDestination(null, maps.GetValueOrDefault));
        Assert.Null(ItemWarpSkills.DungeonWingDestination(maps[410103], _ => null));   
    }

 /// <summary>The Butterfly Wing goes to the bind point, never to the Map_List return point. With no bind it goes to the new-character spawn.</summary>
    [Fact]
    public void ButterflyWing_GoesToTheBind_ThenToTheSpawn()
    {
        var maps = Maps();
        var positions = Positions();
        var p103 = positions[103];
        Assert.Equal(new ItemWarpSkills.Destination(1200, p103.Position_X, p103.Position_Y, p103.Position_Z),
            ItemWarpSkills.ButterflyWingDestination(103, maps.GetValueOrDefault, positions));
        Assert.Equal(new ItemWarpSkills.Destination(WorldEntryService.DefaultMapId, WorldEntryService.DefaultX,
                WorldEntryService.DefaultY, WorldEntryService.DefaultZ),
            ItemWarpSkills.ButterflyWingDestination(0, maps.GetValueOrDefault, positions));
    }

 /// <summary>A town warp's 1509 moves the player only on the grant that the 1561 answer left, for the same skill and map, within the landing window, and only once.</summary>
    [Fact]
    public void Landing_NeedsTheGrant_OfTheSameSkillAndMap_OnceAndInTime()
    {
        var player = new RO2.Server.Zone.Models.PlayerState();
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.PronteraWarp, 1300, t));   

        ItemWarpSkills.GrantLanding(player, ItemWarpSkills.PronteraWarp, 1300, t);
        Assert.False(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.MorrocWarp, 1300, t));     
        Assert.False(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.PronteraWarp, 1100, t));   
        Assert.True(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.PronteraWarp, 1300, t.AddMilliseconds(15)));
        Assert.False(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.PronteraWarp, 1300, t.AddMilliseconds(20)));   

        ItemWarpSkills.GrantLanding(player, ItemWarpSkills.PronteraWarp, 1300, t);
        Assert.False(ItemWarpSkills.TakeLanding(player, ItemWarpSkills.PronteraWarp, 1300,
            t + ItemWarpSkills.LandingWindow + TimeSpan.FromMilliseconds(1)));                      
    }

    private static Dictionary<uint, MapData> Maps() =>
        JsonSerializer.Deserialize<List<MapData>>(File.ReadAllText(Path.Combine(DataDir(), "Map_List.json")), DataJsonOptions)!
            .GroupBy(m => m.Map_Index).ToDictionary(g => g.Key, g => g.First());

    private static Dictionary<uint, PositionData> Positions() =>
        JsonSerializer.Deserialize<List<PositionData>>(File.ReadAllText(Path.Combine(DataDir(), "Position_Info.json")), DataJsonOptions)!
            .GroupBy(p => p.Position_ID).ToDictionary(g => g.Key, g => g.First());

 /// <summary>The repo's data/ folder, found by walking up from the test binaries (works on any machine and in CI).</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Map_List.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Map_List.json");
    }
}
