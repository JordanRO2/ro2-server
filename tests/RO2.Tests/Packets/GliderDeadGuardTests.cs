using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>A dead player's glider requests (1515 and 1528) are ignored: no move, no reply, no broadcast, like /escape.</summary>
public sealed class GliderDeadGuardTests
{
    private const uint MapId = 1100;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadPlayer_GliderRequest_IsIgnored_NothingMovedOrSent(bool farGlider)
    {
        await using var net = await Harness.StartAsync(1);
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, MapId = MapId, X = 1, Y = 2, Z = 3, IsDead = true };
        var players = new PlayerStateManager(new NoDirtySink());
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        // Empty container: any service lookup past the guard (data, repos, broadcast) would throw.
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var body = new byte[12]; // station 0 at body+10

        if (farGlider)
            await new TakeFarGliderHandler(scopes, NullLogger<TakeFarGliderHandler>.Instance, players, broadcast).HandleAsync(net.Sessions[0], body);
        else
            await new GliderTeleportHandler(scopes, NullLogger<GliderTeleportHandler>.Instance, players, broadcast).HandleAsync(net.Sessions[0], body);

        Assert.Equal((MapId, 1f, 2f, 3f), (player.MapId, player.X, player.Y, player.Z));
        Assert.True(player.IsDead);
        Assert.False(player.PendingGliderDismount);
        // Nothing reached the client: a read times out.
        await Assert.ThrowsAsync<TimeoutException>(() => net.Readers[0].NextAsync().WaitAsync(TimeSpan.FromMilliseconds(300)));
    }
}
