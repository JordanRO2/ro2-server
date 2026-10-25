using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Checks the 2499 OnPcTraitChange lines EffectService sends when an effect is applied to a character
/// (<see cref="EffectService.BuildTraitChangeLines"/>), byte for byte against reference frames.
/// </summary>
/// <remarks>
/// The expected bodies are those of the ranking buff 50603, which sends five 2499 lines in the order given
/// by Effect_Table, not sorted by trait.
/// </remarks>
public class EffectTraitLines2499Tests
{
 /// <summary>The five 2499 bodies of the ranking buff 50603 (Trait_Kind 4, 1000.0 each).</summary>
    private static readonly string[] Retail50603Lines =
    {
        "01191900fde300000000040000000000000000007a44abc5000001",   
        "01191900fde300000000040000000100000000007a44abc5000001",   
        "01191900fde300000000040000000400000000007a44abc5000001",   
        "01191900fde300000000040000000200000000007a44abc5000001",   
        "01191900fde300000000040000000300000000007a44abc5000001",   
    };

    [Fact]
    public async Task RankingBuffLinesAreRetailsFiveFramesInTableOrder()
    {
        var effects = await LoadEffectServiceAsync();

        
        
        var lines = effects.BuildTraitChangeLines(50603, effectLevel: 1);

        Assert.Equal(Retail50603Lines.Length, lines.Count);
        for (int i = 0; i < lines.Count; i++)
            Assert.Equal(Convert.FromHexString(Retail50603Lines[i]), lines[i]);
    }

 /// <summary>
 /// Cure's effect 6319 has a tick period and a heal direction but no stat-modifier row, so its application
 /// sends no 2499 line and no closing 2498.
 /// </summary>
    [Fact]
    public async Task EffectWithoutModifierRowsSendsNoLine()
    {
        var effects = await LoadEffectServiceAsync();

        Assert.Empty(effects.BuildTraitChangeLines(6319, effectLevel: 10));
    }

 /// <summary>
 /// An EffectService over the shipped Effect_Table only. <see cref="EffectService.BuildTraitChangeLines"/>
 /// reads only game data, so the other collaborators are not constructed.
 /// </summary>
    private static async Task<EffectService> LoadEffectServiceAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-effect2499-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            
            File.Copy(Path.Combine(RequireDataDirectory(), "Effect_Table.json"), Path.Combine(dir, "Effect_Table.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            Assert.NotEmpty(store.EffectTable);

            return new EffectService(players: null!, broadcast: null!, data: store, npcState: null!, loot: null!,
                scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());
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
