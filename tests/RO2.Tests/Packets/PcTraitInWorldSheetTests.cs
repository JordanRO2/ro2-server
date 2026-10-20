using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The in-world 2498 path: every sender's sheet keeps the active effects, and the runtime max HP/SP and the combat
/// cache follow that sheet.
/// </summary>
/// <remarks>
/// Uses the full shipped data directory, so the gear indices StatService builds come from the real tables.
/// </remarks>
public class PcTraitInWorldSheetTests
{
    private const int BlobStart = 3;   

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadFullStoreAsync);

    private static readonly IReadOnlyList<InventoryItem> NoGear = Array.Empty<InventoryItem>();

    private static Character Character() => new()
    {
        Id = 1, Job = 4, Level = 20, Str = 40, Agi = 20, Int = 10, Wis = 30, Vit = 50, Luk = 1,
        Hp = 700, MaxHp = 700, Mp = 40, MaxMp = 40,
    };

    private static async Task<(EffectService Effects, GameDataStore Store)> ServiceAsync()
    {
        var store = await Store.Value;
        return (new EffectService(players: null!, broadcast: null!, data: store, npcState: null!, loot: null!,
            scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()), store);
    }

    private static PlayerState Player(Character c, int hp, int maxHp, int mp, int maxMp, params (uint Id, byte Level)[] effects)
    {
        var p = new PlayerState { Character = c, Hp = hp, MaxHp = maxHp, Mp = mp, MaxMp = maxMp };
        foreach (var (id, level) in effects)
            p.ActiveEffects.Add(new ActiveEffect { EffectId = id, EffectLevel = level });
        return p;
    }

    private static uint MaxHpOf(byte[] body) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(BlobStart + 8));
    private static uint CurHpOf(byte[] body) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(BlobStart + 72));

    [Fact]
    public async Task WithNoEffectItIsThePlainSheetAndThePlainCombatStats()
    {
        var (effects, store) = await ServiceAsync();
        var c = Character();
        var plain = WorldEntryService.BuildFullPcTrait2498(c, store, NoGear, 300, 20);
        var player = Player(c, 300, 1, 20, 1);

        var body = effects.RefreshPcTraitSheet(player, c, NoGear);

        Assert.Equal(plain, body);
        Assert.Equal((int)MaxHpOf(plain), player.MaxHp);
        var eff = StatService.EffectivePlayerStats(store, c, NoGear);
        Assert.Equal((eff.Str, eff.Vit, eff.Defense, eff.Crit, eff.Parry), (player.CombatStr, player.CombatVit,
            player.CombatDefense, player.CombatCrit, player.CombatParry));
    }

 /// <summary>Effect 40011 (+10% max HP at level 10): the sheet and the server max are +10%, current HP is not raised.</summary>
    [Fact]
    public async Task AMaxHpBuffStaysOnTheSheetAndTheServerMax()
    {
        var (effects, store) = await ServiceAsync();
        var c = Character();
        uint plainMax = MaxHpOf(WorldEntryService.BuildFullPcTrait2498(c, store, NoGear, 300, 20));
        var player = Player(c, 300, (int)plainMax, 20, 40, (40011, 10));

        var body = effects.RefreshPcTraitSheet(player, c, NoGear);

        var expected = EffectService.BuildPcTraitSheet(c, store, StatService.ComputeEquipmentStats(store, NoGear), 300, 20,
            effects.SumSheetModifiers(player.ActiveEffects));
        Assert.Equal(expected, body);
        uint buffed = (uint)Math.Round(plainMax * 1.1, MidpointRounding.ToEven);
        Assert.Equal(buffed, MaxHpOf(body));
        Assert.Equal((int)buffed, player.MaxHp);
        Assert.Equal(300, player.Hp);
        Assert.Equal(300u, CurHpOf(body));
    }

 /// <summary>Effect 50603 (+10% on STR/AGI/INT/WIS/VIT): combat rolls the buffed primaries that the sheet shows.</summary>
    [Fact]
    public async Task APrimaryBuffReachesTheCombatCache()
    {
        var (effects, _) = await ServiceAsync();
        var c = Character();
        var player = Player(c, 300, 1, 20, 1, (50603, 1));

        var body = effects.RefreshPcTraitSheet(player, c, NoGear);

        var blob = body.AsSpan(BlobStart);
        Assert.Equal(44, BinaryPrimitives.ReadUInt16LittleEndian(blob[14..]));   
        Assert.Equal(44, player.CombatStr);
        Assert.Equal(22, player.CombatAgi);
        Assert.Equal(11, player.CombatInt);
        Assert.Equal(33, player.CombatWis);
        Assert.Equal(55, player.CombatVit);
        Assert.Equal(1, player.CombatLuk);                                       
        Assert.Equal((int)MaxHpOf(body), player.MaxHp);
    }

    [Fact]
    public async Task AMapLoadKeepsAPlayerAtFullAtTheBuffedMax()
    {
        var (effects, store) = await ServiceAsync();
        var c = Character();
        int plainMax = (int)MaxHpOf(WorldEntryService.BuildFullPcTrait2498(c, store, NoGear, 1, 1));
        var mapLoad = Player(c, plainMax, plainMax, 40, 40, (40011, 10));
        var swap = Player(c, plainMax, plainMax, 40, 40, (40011, 10));

        var mapBody = effects.RefreshMapLoadPcTraitSheet(mapLoad, c, NoGear);
        effects.RefreshPcTraitSheet(swap, c, NoGear);

        Assert.Equal(mapLoad.MaxHp, mapLoad.Hp);
        Assert.Equal((uint)mapLoad.MaxHp, CurHpOf(mapBody));
        Assert.True(mapLoad.MaxHp > plainMax);
        Assert.Equal(plainMax, swap.Hp);   
    }

    [Fact]
    public async Task ARemovalCutsCurrentHpToThePlainMax()
    {
        var (effects, store) = await ServiceAsync();
        var c = Character();
        int plainMax = (int)MaxHpOf(WorldEntryService.BuildFullPcTrait2498(c, store, NoGear, 1, 1));
        var player = Player(c, plainMax + 50, plainMax + 50, 40, 40);   

        effects.RefreshPcTraitSheet(player, c, NoGear);

        Assert.Equal(plainMax, player.MaxHp);
        Assert.Equal(plainMax, player.Hp);
    }

    [Fact]
    public async Task AReviewSheetChangesNothingOnTheTarget()
    {
        var (effects, _) = await ServiceAsync();
        var c = Character();
        var target = Player(c, 300, 5, 20, 7, (40011, 10));

        var body = effects.BuildInWorldPcTrait(target, c, NoGear);

        Assert.Equal((300, 5, 20, 7), (target.Hp, target.MaxHp, target.Mp, target.MaxMp));
        Assert.Equal(0, target.CombatStr);
        Assert.Equal(effects.RefreshPcTraitSheet(Player(c, 300, 5, 20, 7, (40011, 10)), c, NoGear), body);
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
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }
}
