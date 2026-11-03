using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Characters.Domain;
using RO2.Companions.Application;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

public class RankingCounterAdapterTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    // Without a ranking repository or game data the count is a no-op (as the direct RankingService call was), online or not.
    [Fact]
    public async Task Count_WithoutRepository_IsANoOp_ForOnlineAndUnknownCharacters()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        IRankingCounter counter = new RankingCounterAdapter(new ServiceCollection().BuildServiceProvider(), players, NullLogger.Instance);

        await counter.CountAsync(114, 1);
        await counter.CountAsync(0, 99);
    }
}
