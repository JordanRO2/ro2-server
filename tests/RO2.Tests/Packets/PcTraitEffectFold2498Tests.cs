using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Wire tests for the 2498 OnPcTrait sheet that EffectService sends when an effect is applied or removed, with the
/// active effects' Trait_Kind 3 and 4 rows folded in.
/// </summary>
/// <remarks>
/// Each case compares the sheet before an effect run with the sheet after it. Only the fields named by the run's rows
/// may differ.
/// </remarks>
public class PcTraitEffectFold2498Tests
{
    private const int BlobStart = 3;   

 /// <summary>Sheet before the run of effect 40011: max HP 5,648, no stat row active.</summary>
    private const string RetailPlain =
        "028c008c00cce300000000101600001a07c900c900c90065006500000000000000000000007701000000005a020000ea03000000000000000001000000b10200000000000000000000803f101600001a07080005000000000000005c02ca00c9009201000000000000000000000000000000000000000000000000000000000000000000000000c800000000000000";

 /// <summary>Sheet closing the run of effect 40011: max HP 6,213, current HP still 5,648.</summary>
    private const string RetailAfter40011 =
        "028c008c00cce300000000451800001a07c900c900c90065006500000000000000000000007701000000005a020000ea03000000000000000001000000b10200000000000000000000803f101600001a07080005000000000000005c02ca00c9009201000000000000000000000000000000000000000000000000000000000000000000000000c800000000000000";

 /// <summary>Sheet closing the run of effect 40012 while 40011 stays active: the two max stat fields rise from 602 and 1,002 to 662 and 1,102.</summary>
    private const string RetailAfter40012 =
        "028c008c00cce300000000451800001a07c900c900c9006500650000000000000000000000770100000000960200004e04000000000000000001000000b10200000000000000000000803f101600001a07080005000000000000005c02ca00c9009201000000000000000000000000000000000000000000000000000000000000000000000000c800000000000000";

 /// <summary>Sheets before and after the run of effect 60037, whose single flat max HP row raises max HP from 43,126 to 44,126.</summary>
    private const string RetailBefore60037 =
        "028c008c00cce30000000076a8000043267b15be0368028d02d90b0000770100000000000000000000000087430000900100000000c341000001000000a30100000000000000000000404076a800004326b8001e0000009019a30190487d07e60bca15a30a78007c0200000000000000000000000000000000000000000000000000002403d0097800000000000000";
    private const string RetailAfter60037 =
        "028c008c00cce3000000005eac000043267b15be0368028d02d90b0000770100000000000000000000000087430000900100000000c341000001000000a30100000000000000000000404076a800004326b8001e0000009019a30190487d07e60bca15a30a78007c0200000000000000000000000000000000000000000000000000002403d0097800000000000000";

    private static EffectService.EffectSheetModifiers Percent(params (byte Trait, double Value)[] rows)
        => new(new Dictionary<byte, double>(), rows.ToDictionary(r => r.Trait, r => r.Value));

    private static EffectService.EffectSheetModifiers Flat(params (byte Trait, double Value)[] rows)
        => new(rows.ToDictionary(r => r.Trait, r => r.Value), new Dictionary<byte, double>());

    [Fact]
    public void AMaxHpPercentRowGivesRetailsClosingSheet()
    {
        var body = EffectService.FoldSheetModifiers(Convert.FromHexString(RetailPlain), Percent((21, 1000.0)),
            currentHp: 5648, currentSp: 1818);

        Assert.Equal(Convert.FromHexString(RetailAfter40011), body);
    }

    [Fact]
    public void AnAttackPercentRowGivesRetailsClosingSheet()
    {
        var body = EffectService.FoldSheetModifiers(Convert.FromHexString(RetailAfter40011),
            Percent((23, 1000.0), (25, 1000.0)), currentHp: 5648, currentSp: 1818);

        Assert.Equal(Convert.FromHexString(RetailAfter40012), body);
    }

    [Fact]
    public void AFlatMaxHpRowAddsItsAmount()
    {
        var body = EffectService.FoldSheetModifiers(Convert.FromHexString(RetailBefore60037), Flat((21, 1000.0)),
            currentHp: 43126, currentSp: 9795);

        Assert.Equal(Convert.FromHexString(RetailAfter60037), body);
    }

 /// <summary>
 /// End to end over the shipped Effect_Table: effects 40011 and 40012 at level 10, both active and folded into the
 /// plain sheet, produce the expected sheet with both applied.
 /// </summary>
    [Fact]
    public async Task TheShippedRowsOfTwoActiveBuffsGiveRetailsSheet()
    {
        var effects = new EffectService(players: null!, broadcast: null!, data: await LoadStoreAsync("Effect_Table.json"),
            npcState: null!, loot: null!, scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());

        var modifiers = effects.SumSheetModifiers(new[]
        {
            new ActiveEffect { EffectId = 40011, EffectLevel = 10 },
            new ActiveEffect { EffectId = 40012, EffectLevel = 10 },
            new ActiveEffect { EffectId = 6319, EffectLevel = 10 },   
        });

        Assert.Empty(modifiers.Flat);
        Assert.Equal(3, modifiers.Percent.Count);
        Assert.Equal(1000.0, modifiers.Percent[21]);
        Assert.Equal(1000.0, modifiers.Percent[23]);
        Assert.Equal(1000.0, modifiers.Percent[25]);
        Assert.Equal(Convert.FromHexString(RetailAfter40012),
            EffectService.FoldSheetModifiers(Convert.FromHexString(RetailPlain), modifiers, 5648, 1818));
    }

 /// <summary>Rows for traits the sheet has no slot for, such as walk and move speed, are left out of it.</summary>
    [Fact]
    public async Task RowsOffTheSheetAreLeftOut()
    {
        var effects = new EffectService(players: null!, broadcast: null!, data: await LoadStoreAsync("Effect_Table.json"),
            npcState: null!, loot: null!, scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());

        var modifiers = effects.SumSheetModifiers(new[] { new ActiveEffect { EffectId = 60037, EffectLevel = 1 } });

        var row = Assert.Single(modifiers.Flat);
        Assert.Equal((byte)21, row.Key);
        Assert.Equal(1000.0, row.Value);
        Assert.Empty(modifiers.Percent);
    }

 /// <summary>
 /// Removal: the sheet without the buff carries the lower max, and current HP above it is clamped to that max.
 /// </summary>
    [Fact]
    public void RemovingTheBuffCutsCurrentHpToThePlainMax()
    {
        var body = EffectService.FoldSheetModifiers(Convert.FromHexString(RetailPlain),
            EffectService.EffectSheetModifiers.None, currentHp: 6213, currentSp: 1818);

        Assert.Equal(Convert.FromHexString(RetailPlain), body);
    }

 /// <summary>A u16 stat saturates at 65,535: a buff that would exceed it leaves the field at the maximum.</summary>
    [Fact]
    public void AU16StatSaturates()
    {
        var plain = Convert.FromHexString(RetailPlain);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(BlobStart + 44), 62035);

        var body = EffectService.FoldSheetModifiers(plain, Percent((25, 1000.0)), 5648, 1818);

        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(BlobStart + 44)));
    }

 /// <summary>
 /// With no effect rows the builder produces the plain in-world sheet, byte for byte.
 /// </summary>
    [Fact]
    public async Task WithNoRowItIsThePlainSheet()
    {
        var store = await LoadStoreAsync("PCBaseStatus.json");
        var character = Character();

        var body = EffectService.BuildPcTraitSheet(character, store, StatService.EquipStats.Empty, 700, 40,
            EffectService.EffectSheetModifiers.None);

        Assert.Equal(WorldEntryService.BuildFullPcTrait2498(character, store, null, 700, 40), body);
    }

 /// <summary>
 /// A primary-stat row goes through the class formula like a gear primary: +10% on STR/AGI/INT/WIS/VIT raises the
 /// primaries and, through them, max HP/SP and the derived stats. Effect 50603 takes STR from 201 to 221 and max HP
 /// from 6,213 to 6,346.
 /// </summary>
    [Fact]
    public async Task APrimaryRowCascadesThroughTheClassFormula()
    {
        var store = await LoadStoreAsync("PCBaseStatus.json");
        var character = Character();
        var modifiers = Percent((0, 1000.0), (1, 1000.0), (2, 1000.0), (3, 1000.0), (4, 1000.0));

        var body = EffectService.BuildPcTraitSheet(character, store, StatService.EquipStats.Empty, 700, 40, modifiers);
        var blob = body.AsSpan(BlobStart);

        int[] buffed = { 44, 22, 11, 33, 55 };   
        for (int i = 0; i < buffed.Length; i++)
            Assert.Equal(buffed[i], BinaryPrimitives.ReadUInt16LittleEndian(blob[(14 + 2 * i)..]));

        var expected = StatService.ComputeBaseStats(store, character.Job, character.Level,
            buffed[0], buffed[1], buffed[2], buffed[3], buffed[4], character.Luk);
        Assert.True(expected.MaxHp > 0, "job 4 has a PCBaseStatus row");
        Assert.Equal(expected.MaxHp, BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]));
        Assert.Equal(expected.MaxSp, BinaryPrimitives.ReadUInt16LittleEndian(blob[12..]));
        Assert.Equal(expected.Derived[23], BinaryPrimitives.ReadUInt16LittleEndian(blob[40..]));
        Assert.Equal(expected.Derived[25], BinaryPrimitives.ReadUInt16LittleEndian(blob[44..]));
        
        Assert.Equal(700u, BinaryPrimitives.ReadUInt32LittleEndian(blob[72..]));
    }

    private static Character Character() => new()
    {
        Job = 4, Level = 20, Str = 40, Agi = 20, Int = 10, Wis = 30, Vit = 50, Luk = 1,
        Hp = 700, MaxHp = 700, Mp = 40, MaxMp = 40,
    };

 /// <summary>
 /// A GameDataStore loaded with only the named shipped tables. Missing files are tolerated, and the store stays partial
 /// so that StatService's process-wide gear indices are not built from it.
 /// </summary>
    private static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-effect2498-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var data = RequireDataDirectory();
            foreach (var table in tables)
                File.Copy(Path.Combine(data, table), Path.Combine(dir, table));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
