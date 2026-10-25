using RO2.Economy.Domain;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for corpse and auction details: the shared NPC record's corpse-gatherable byte (+66) and camp class (+72) for 2317, 2319 and 2506; the auction comparer's tail (template, expiry, id) for 1400, 2456, and the 2455 and 2457 lists; the per-viewer split of the corpse 2507; and the slots of the 2592 NfyKharaTitleChange.
/// </summary>
/// <remarks>
/// Where a test pins a rule rather than a reference frame, it says so.
/// </remarks>
public class Reaudit2_A2_Tests
{
    
 /// <summary>
 /// A 2317 OnRegenNPC of a gatherable NPC (Camp 2, with an AfterDeath_FuncObject_ID): level 1, navmesh cell 25, walk speed 1.0, HP 51/51, SP 42/42, record+66 = 1 and record+72 = 3.
 /// </summary>
    private const string RetailGatherableRegen =
        "015b5b009ae2000000001375ce01503db10201135500003759000019000000135500003759000019000000000000c8420000803f"
        + "33000000330000002a0000002a00000000000000000000000100000000000300000000000000000000";

    private static NpcRuntimeState Npc(uint handle, uint asset, int camp, bool gatherable) => new()
    {
        EntityOid = handle,
        NpcId = asset,
        MapId = 1100u,
        Level = 1,
        Hp = 51,
        MaxHp = 51,
        MaxSp = 42,
        Camp = camp,
        CorpseGatherable = gatherable,
        NavCell = 25,
        PosX = 217.79f,
        PosY = 228.39f,
        Spawn = new SpawnEntry { MapId = 1100u, Index = 1, AssetIndex = asset, PosX = 217.79f, PosY = 228.39f, Rot = 100.0f },
    };

    [Fact]
    public void Regen2317_GatherableCampTwo_IsTheRetailFrameByteForByte()
    {
        
        var body = NpcLifecyclePackets.BuildRegenNpc2317(Npc(0x01CE7513u, 45170000u, camp: 2, gatherable: true), walkSpeed: 1.0f);
        Assert.Equal(Convert.FromHexString(RetailGatherableRegen), body);
        Assert.Equal(1, body[10 + 66]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10 + 72)));
    }

    [Fact]
    public void Regen2317_NotGatherable_CarriesZeroAt66()
    {
        var body = NpcLifecyclePackets.BuildRegenNpc2317(Npc(0x01CE7513u, 45170000u, camp: 2, gatherable: false), walkSpeed: 1.0f);
        var expected = Convert.FromHexString(RetailGatherableRegen);
        expected[10 + 66] = 0;
        Assert.Equal(expected, body);
    }

    [Theory]
    
    
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public void CampClass_FollowsTheArchiveSplit(int camp, byte expected)
    {
        Assert.Equal(expected, NpcSpawnService.CampByte(Npc(0x00015D13u, 30060000u, camp, gatherable: false)));
    }

    [Fact]
    public void ViewData2319_CampThree_CarriesOneAt72()
    {
        
        
        var body = NpcSpawnService.BuildOnNpcViewData2319(Npc(0x00015D13u, 30060000u, camp: 3, gatherable: false), walkSpeed: 1.0f);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10 + 72)));
        Assert.Equal(0, body[10 + 66]);
        Assert.Equal(0, body[10 + 68]);
    }

    [Fact]
    public async Task Tracking_SetsGatherableFromNpcInfo_AndTheRespawnAndCorpseKeepIt()
    {
        const uint map = 900771;
        var dir = Path.Combine(Path.GetTempPath(), "ro2-gather-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NpcStateManager npcs;
        try
        {
            File.Copy(Path.Combine(RequireDataDirectory(), "NPCInfo.json"), Path.Combine(dir, "NPCInfo.json"));
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ZoneSpawn.json"),
                $$"""[{"MapId":{{map}},"Index":1,"PosX":10.5,"PosY":20.25,"PosZ":0,"Rot":0,"AssetIndex":45170000,"HoverRadius":0,"Triggered":0,"SpawnRadius":0},{"MapId":{{map}},"Index":2,"PosX":11.5,"PosY":21.25,"PosZ":0,"Rot":0,"AssetIndex":50003340,"HoverRadius":0,"Triggered":0,"SpawnRadius":0}]""");
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
            npcs.InitializeMap(map);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var gather = npcs.GetNpc(npcs.AllocateHandle(map, 1))!;
        var plain = npcs.GetNpc(npcs.AllocateHandle(map, 2))!;
        Assert.True(gather.CorpseGatherable);
        Assert.False(plain.CorpseGatherable);

        var h1 = gather.EntityOid;
        Assert.True(npcs.ApplyDamage(h1, 1_000_000).Died);
        var stamp = gather.DeathTime!.Value;
        var pending = LootService.TrackPendingCorpse(h1, stamp);
        try
        {
            var h2 = npcs.Respawn(h1);
            Assert.True(npcs.GetNpc(h2)!.CorpseGatherable);   
            var corpse = npcs.GetNpc(h1)!;                     
            Assert.True(corpse.IsDead);
            Assert.True(corpse.CorpseGatherable);
        }
        finally
        {
            LootService.ReleasePendingCorpse(h1, pending);
        }
    }

    
    private const ulong ArticleBase = 1_040_000_000_000_000_000UL;

    private static AuctionPackets.SearchKey Key(ulong uid, uint template, long current, long instant, long minutes)
        => new(uid - ArticleBase, template, current, instant, minutes, minutes * TimeSpan.TicksPerMinute);

    [Fact]
    public void TiedSameTemplate_MostRemainingTimeFirst_EvenWhenOlder_ArchiveCf625f50()
    {
        
        
        
        
        
        var retail = new[]
        {
            Key(1040000000024637358, 22310792, 40100, 50101, 3806),
            Key(1040000000024637360, 22310792, 40100, 50101, 926),
            Key(1040000000024637359, 22310792, 40100, 50101, 926),
        };
        var sorted = AuctionPackets.OrderSearchKeys(Enumerable.Reverse(retail), AuctionPackets.SearchOrder.Default, _ => "Mummy Card");
        Assert.Equal(retail.Select(k => k.Id), sorted.Select(k => k.Id));
    }

    [Fact]
    public void TiedDifferentTemplates_TemplateAscendingBeforeTheExpiry_ArchiveA7cf380c()
    {
        
        
        
        
        var retail = new[]
        {
            Key(1040000000024637665, 60000730, 5555555100, 5555555101, 9713),
            Key(1040000000024637685, 70000660, 5555555100, 5555555101, 9745),
            Key(1040000000024637666, 70000660, 5555555100, 5555555101, 9713),
        };
        var sorted = AuctionPackets.OrderSearchKeys(Enumerable.Reverse(retail), AuctionPackets.SearchOrder.Default, _ => "Lucky Powder");
        Assert.Equal(retail.Select(k => k.Id), sorted.Select(k => k.Id));
    }

    [Fact]
    public void OrderForList_UsesTheExpiryFromTheArticle()
    {
        
        var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var older = new AuctionArticle { Id = 1, ItemId = 22310792, StartPrice = 40100, BuyoutPrice = 50101, ExpiresAt = now.AddMinutes(3806) };
        var newer = new AuctionArticle { Id = 2, ItemId = 22310792, StartPrice = 40100, BuyoutPrice = 50101, ExpiresAt = now.AddMinutes(926) };
        var ordered = AuctionPackets.OrderForList(new[] { newer, older }, _ => "Mummy Card");
        Assert.Equal(new ulong[] { 1, 2 }, ordered.Select(a => a.Id));
    }

    
    [Fact]
    public void CorpseAudience_SplitsLootersFromBystanders_AndEachViewerGetsOne2507()
    {
        
        var audience = LootService.CorpseAudience.Capture(new uint[] { 9, 5, 7, 11, 9 }, killerSession: 5, sharerSessions: new uint[] { 7 });
        Assert.Equal(new uint[] { 5, 7 }, audience.Looters);
        Assert.Equal(new uint[] { 9, 11 }, audience.Bystanders);

        
        Assert.Equal(new uint[] { 9, 11 }, audience.Claim(audience.Bystanders));
        Assert.Equal(new uint[] { 5, 7 }, audience.Claim(audience.All));
        Assert.Empty(audience.Claim(audience.All));
        Assert.Empty(audience.Claim(audience.Bystanders));
    }

    [Fact]
    public void CorpseAudience_LootClockFirst_CoversTheBystandersToo()
    {
        var audience = LootService.CorpseAudience.Capture(new uint[] { 5, 9 }, killerSession: 5, sharerSessions: Array.Empty<uint>());
        Assert.Equal(new uint[] { 5, 9 }, audience.Claim(audience.All));
        Assert.Empty(audience.Claim(audience.Bystanders));
    }

    [Fact]
    public void BystanderDelay_CountsTheStayFromTheDeath()
    {
        
        
        var died = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var stay = TimeSpan.FromSeconds(45);
        Assert.Equal(TimeSpan.FromSeconds(44.75), LootService.CorpseAudience.BystanderDelay(died, stay, died.AddMilliseconds(250)));
        Assert.Equal(TimeSpan.Zero, LootService.CorpseAudience.BystanderDelay(died, stay, died.AddSeconds(60)));
        Assert.Equal(stay, LootService.CorpseAudience.BystanderDelay(null, stay, died));
    }

    
    private static readonly DateTime T0 = new(2026, 9, 22, 22, 34, 18, DateTimeKind.Utc);

    private static bool Any(uint _) => true;

    [Fact]
    public void ReSelectInsideTheTimer_ASlotNotApplied_Live3364()
    {
        
        
        
        var sel = KharaTitleStore.Decide(new KharaTitleState(0x244, 0x268, null, null), 0x23C, 0x268, T0, Any);
        Assert.Equal(0x23Cu, sel.SlotA);
        var again = KharaTitleStore.Decide(sel.NewState, 0x23C, 0x26B, T0.AddMilliseconds(11391), Any);
        Assert.True(again.Accepted);
        Assert.Equal(Convert.FromHexString("11c6aa0effffffff6b020000"), KharaTitleSelectPackets.Build2592(0x0EAAC611u, again));
        Assert.Equal(Convert.FromHexString("3c02000041a801006b02000000000000"), KharaTitleSelectPackets.Build2582(0x23C, 0x26B, again));
        Assert.Equal(new KharaTitleState(0x23C, 0x26B, T0, T0.AddMilliseconds(11391)), again.NewState);
    }

    [Fact]
    public void ClearInsideTheCooldown_BSlotNotApplied_ThenEchoes_Live0452()
    {
        
        
        
        
        const uint handle = 0x0E426411u;
        var sel = KharaTitleStore.Decide(new KharaTitleState(0x23C, 0x22A, null, null), 0x244, 0x22A, T0, Any);
        Assert.Equal(Convert.FromHexString("1164420e440200002a020000"), KharaTitleSelectPackets.Build2592(handle, sel));

        var clear = KharaTitleStore.Decide(sel.NewState, 0, 0x22A, T0.AddMilliseconds(5172), Any);
        Assert.True(clear.Accepted);
        Assert.Equal(Convert.FromHexString("000000008cc001002a02000000000000"), KharaTitleSelectPackets.Build2582(0, 0x22A, clear));
        Assert.Equal(Convert.FromHexString("1164420e00000000ffffffff"), KharaTitleSelectPackets.Build2592(handle, clear));
        Assert.Equal(0x22Au, clear.NewState.KharaB);

        var later = KharaTitleStore.Decide(clear.NewState, 0, 0x22A, T0.AddMilliseconds(45781), Any);
        Assert.Equal(Convert.FromHexString("00000000eb2101002a02000000000000"), KharaTitleSelectPackets.Build2582(0, 0x22A, later));
        Assert.Equal(Convert.FromHexString("1164420e000000002a020000"), KharaTitleSelectPackets.Build2592(handle, later));
    }

    [Fact]
    public void ReSelectWithBFromZero_ASlotNotApplied_Live00cca2d7()
    {
        
        
        
        const uint handle = 0x0EC81B11u;
        var zero = KharaTitleStore.Decide(default, 0, 0, T0, Any);
        Assert.Equal(Convert.FromHexString("111bc80e0000000000000000"), KharaTitleSelectPackets.Build2592(handle, zero));
        var sel = KharaTitleStore.Decide(zero.NewState, 0x244, 0, T0.AddMilliseconds(3342), Any);
        Assert.Equal(Convert.FromHexString("44020000c0d401000000000000000000"), KharaTitleSelectPackets.Build2582(0x244, 0, sel));
        Assert.Equal(Convert.FromHexString("111bc80e4402000000000000"), KharaTitleSelectPackets.Build2592(handle, sel));
        var again = KharaTitleStore.Decide(sel.NewState, 0x244, 0x265, T0.AddMilliseconds(3342 + 3232), Any);
        Assert.True(again.Accepted);
        Assert.Equal(Convert.FromHexString("111bc80effffffff65020000"), KharaTitleSelectPackets.Build2592(handle, again));
        Assert.Equal(0x265u, again.NewState.KharaB);
    }

    [Fact]
    public void DifferentAInsideTheTimer_IsNotApplied_RulePin()
    {
        
        
        
        var sel = KharaTitleStore.Decide(default, 0x24C, 0, T0, Any).NewState;
        var other = KharaTitleStore.Decide(sel, 0x256, 0, T0.AddSeconds(30), Any);
        Assert.True(other.Accepted);
        Assert.Equal(KharaTitleStore.SlotNotApplied, other.SlotA);
        Assert.Equal(0x24Cu, other.NewState.KharaA);
        Assert.Equal(T0, other.NewState.ATimerStartUtc);
        Assert.Equal(90000u, other.Timer);
    }

    [Fact]
    public void DifferentAInsideTheTimerAndTheCooldown_AppliesNothing_IsRefused()
    {
        
        
        
        var sel = KharaTitleStore.Decide(new KharaTitleState(0x24C, 0x1F1, null, null), 0x24C, 0x22B, T0, Any).NewState;
        var blocked = KharaTitleStore.Decide(sel, 0x256, 0x22B, T0.AddMilliseconds(2000), Any);
        Assert.False(blocked.Accepted);
        Assert.Equal(Convert.FromHexString("56020000401f00002b020000f7540000"), KharaTitleSelectPackets.Build2582(0x256, 0x22B, blocked));
        Assert.Equal(sel, blocked.NewState);
    }

 /// <summary>The repository's data/ folder, found by walking up from the test output.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "NPCInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/NPCInfo.json");
    }
}
