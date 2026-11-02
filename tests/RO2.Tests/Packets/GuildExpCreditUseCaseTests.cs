using RO2.GameData.Tables;
using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The Guild.Application exp credit: levels derived around the ledger credit, no host involved.</summary>
public class GuildExpCreditUseCaseTests
{
    private sealed class FakeLedger : IGuildExpLedger
    {
        public Task<(ulong Before, ulong After)> CreditMemberAsync(Guild guild, uint charId, ulong amount)
            => Task.FromResult(GuildProgression.CreditMemberExp(guild, charId, amount));
    }

    private static readonly GuildExpTable[] Table =
    {
        new() { Guild_Base_Level = 1, Guild_Base_Exp_Next = 100 },
        new() { Guild_Base_Level = 2, Guild_Base_Exp_Next = 200 },
        new() { Guild_Base_Level = 3, Guild_Base_Exp_Next = 0 },
    };

    [Fact]
    public async Task CreditAsync_ReportsTheLevelCrossed()
    {
        var g = new Guild { Exp = 90 };
        g.Members.Add(new GuildMember { CharId = 1 });
        var useCase = new GuildExpCreditUseCase(new FakeLedger());

        var crossed = await useCase.CreditAsync(g, 1, 15, Table);
        Assert.Equal(new GuildExpCredit(90, 105, 1, 2), crossed);
        Assert.True(crossed.LevelledUp);
        Assert.Equal(15, g.Members[0].ContribPoints);

        var within = await useCase.CreditAsync(g, 1, 5, Table);
        Assert.Equal(new GuildExpCredit(105, 110, 2, 2), within);
        Assert.False(within.LevelledUp);
    }
}
