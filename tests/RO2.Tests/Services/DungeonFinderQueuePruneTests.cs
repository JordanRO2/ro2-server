using RO2.GameData.Tables;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Pins that <see cref="DungeonFinderQueue.Prune"/> reports every entry it removes, even when the predicate flips between reads.</summary>
public class DungeonFinderQueuePruneTests
{
    [Fact]
    public void Prune_RemovesExactlyTheReturnedEntries_WhenThePredicateIsNotStable()
    {
        var queue = new DungeonFinderQueue();
        var row = new FindDungeonList { List_ID = 21, Party_Num = 3 };
        var player = new PlayerState { SessionId = 1, CharacterId = 7, Name = "pc" };
        queue.Join(player, 1, [row]);

        int calls = 0;
        var gone = queue.Prune(_ => ++calls == 1);

        Assert.Single(gone);
        Assert.Empty(queue.MembersOf(21));
    }
}
