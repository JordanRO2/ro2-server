using RO2.Colosseum.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Stateless tests of the Colosseum scoreboard ordering, clamp and ranking byte.</summary>
public class ColosseumScoreboardTests
{
    [Fact]
    public void Order_RegistrantsFirstDeduped_ThenRestByScoreDescending()
    {
        var scores = new Dictionary<string, int> { ["A"] = 5, ["B"] = 9, ["C"] = 7, ["D"] = 1 };
        var rows = ColosseumScoreboard.Order(new[] { "D", "", "A", "D" }, scores);
        Assert.Equal(new[] { "D", "A", "B", "C" }, rows.Select(r => r.Name));
        Assert.Equal(new byte[] { 1, 5, 9, 7 }, rows.Select(r => r.Score));
    }

    [Fact]
    public void Order_ClampsTo255_AndMissingRegistrantIsZero()
    {
        var scores = new Dictionary<string, int> { ["Big"] = 300, ["Neg"] = -4 };
        var rows = ColosseumScoreboard.Order(new[] { "Ghost" }, scores);
        Assert.Equal(new ColosseumStanding("Ghost", 0), rows[0]);
        Assert.Equal(new ColosseumStanding("Big", 255), rows[1]);
        Assert.Equal(new ColosseumStanding("Neg", 0), rows[2]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 255)]
    [InlineData(256, 0)]
    [InlineData(257, 1)]
    public void RankingByte_IsLowByte(int n, int expected)
        => Assert.Equal((byte)expected, ColosseumScoreboard.RankingByte(n));
}
