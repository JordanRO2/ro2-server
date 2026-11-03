using Microsoft.Extensions.Hosting;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Starts the ranking reset clock at host boot, so scheduled rolls happen on time even when no client reads a board.
/// The clock itself stops with ApplicationStopping, so there is nothing to do on stop.
/// </summary>
public sealed class RankingClockHostedService : IHostedService
{
    private readonly IServiceProvider _services;

    public RankingClockHostedService(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RankingService.EnsureClockRunning(_services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
