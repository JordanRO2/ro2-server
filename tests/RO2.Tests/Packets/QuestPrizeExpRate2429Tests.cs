using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// EXP paid by a quest prize (2429 OnQuestPrize, blob+16): Quest_Info Reward_EXP raised by the player's active Exp_Rate effects. EffectService.SumExpRate and ApplyExpRate supply the rate, and QuestPrizeHandler.GrantRewardsAsync uses it both for the 2429 and for the EXP it grants.
/// </summary>
/// <remarks>
/// Frames are framed with the header rule for the connection's encryption state (3 bytes unencrypted, 5 bytes otherwise), and the opcode is read from the frame bytes. The reference case has three Exp_Rate effect lines (effects 40009, 102001 and 105010) that together give a rate of 6,000 (+60%).
/// </remarks>
public class QuestPrizeExpRate2429Tests
{
 /// <summary>The three active lines at the levels that give the reference values: 40009 is Level x 100 (level 10), 102001 is Level x 2000 (level 1), and 105010 is a flat 3000.</summary>
    private static readonly ActiveEffect[] SessionEffects =
    {
        new() { EffectId = 40009, EffectLevel = 10 },
        new() { EffectId = 102001, EffectLevel = 1 },
        new() { EffectId = 105010, EffectLevel = 1 },
    };

    [Fact]
    public async Task ActiveLinesAreRetailsAndSumToTheRate()
    {
        var effects = await LoadEffectServiceAsync();

        Assert.Equal(Convert.FromHexString("01191900fde300000000040000004500000000007a44499c000001"),
            Assert.Single(effects.BuildTraitChangeLines(40009, 10)));
        Assert.Equal(Convert.FromHexString("01191900fde30000000004000000450000000000fa44718e010001"),
            Assert.Single(effects.BuildTraitChangeLines(102001, 1)));
        
        Assert.Contains("01191900fde300000000040000004500000000803b45329a010001",
            effects.BuildTraitChangeLines(105010, 1).Select(Convert.ToHexStringLower));

        Assert.Equal(6000.0, effects.SumExpRate(SessionEffects));
        Assert.Equal(0.0, effects.SumExpRate(Array.Empty<ActiveEffect>()));
        
        Assert.Equal(0.0, effects.SumExpRate(new[] { new ActiveEffect { EffectId = 50603 } }));
    }

 /// <summary>
 /// The three prizes of the reference case: Reward_EXP 276,000, 285,000 and 1,516,000 at +60% pay 441,600, 456,000 and 2,425,600 on the wire. Reward_Money and Reward_Dia are unaffected by the rate.
 /// </summary>
    [Theory]
    [InlineData(613000100u, 276000, 12000u, "011c1c00bfe30000000000000000a4a3892400bd0600e02e000000000000")]
    [InlineData(613000120u, 285000, 12500u, "011c1c00bfe30000000000000000b8a3892440f50600d430000000000000")]
    [InlineData(613000130u, 1516000, 13200u, "011c1c00bfe30000000000000000c2a38924000325009033000000000000")]
    public async Task BoostedPrizeIsRetailsFrame(uint questId, int rewardExp, uint rewardMoney, string retailHex)
    {
        var effects = await LoadEffectServiceAsync();

        var exp = EffectService.ApplyExpRate(rewardExp, effects.SumExpRate(SessionEffects));

        Assert.Equal(Convert.FromHexString(retailHex), QuestProgressPackets.BuildQuestPrize2429(questId, exp, rewardMoney));
    }

 /// <summary>
 /// With no rate active the prize pays Reward_EXP itself (quest 123000024: 180,000 EXP and 1,000 zeny).
 /// </summary>
    [Fact]
    public void UnboostedPrizeIsRewardExp()
    {
        Assert.Equal(180000u, EffectService.ApplyExpRate(180000, 0));
        Assert.Equal(Convert.FromHexString("011c1c00bfe30000000000000000d8d4540720bf0200e803000000000000"),
            QuestProgressPackets.BuildQuestPrize2429(123000024, EffectService.ApplyExpRate(180000, 0), 1000));
        Assert.Equal(0u, EffectService.ApplyExpRate(0, 6000));
        Assert.Equal(0u, EffectService.ApplyExpRate(180000, -10000));
    }

 /// <summary>An EffectService over the shipped Effect_Table only, since the sums read nothing else.</summary>
    private static async Task<EffectService> LoadEffectServiceAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-exprate2429-" + Guid.NewGuid().ToString("N"));
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
