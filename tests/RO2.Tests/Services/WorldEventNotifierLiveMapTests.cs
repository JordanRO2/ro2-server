using Microsoft.Extensions.Logging.Abstractions;
using RO2.Characters.Domain;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The 3030 stage push reads the recipient's map when it sends, not when the recipient list was taken.</summary>
public class WorldEventNotifierLiveMapTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static (WorldEventNotifier Notifier, PlayerState Player) Create()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState { MapId = 1100 };
        players.AddPlayer(7, player);
        var notifier = new WorldEventNotifier(null!, players, new BroadcastService(players, NullLogger<BroadcastService>.Instance));
        return (notifier, player);
    }

    [Fact]
    public void PlayerWhoChangedMapAfterTheSnapshotGetsTheNewMap()
    {
        var (notifier, player) = Create();
        var snapshot = new WorldRecipient(7, 1100);

        player.MapId = 1221; // map change lands while the push is still going out

        Assert.Equal(1221u, notifier.LiveMapId(snapshot));
    }

    [Fact]
    public void RecipientWhoLeftTheRegistryFallsBackToTheSnapshotMap()
    {
        var (notifier, _) = Create();

        Assert.Equal(1100u, notifier.LiveMapId(new WorldRecipient(99, 1100)));
    }
}
