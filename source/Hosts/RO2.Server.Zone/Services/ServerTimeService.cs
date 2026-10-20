using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Periodic server-time keepalive (RMI 2811) for in-world players, with colosseum schedule and timed-
/// vehicle notices.
/// </summary>
public class ServerTimeService : BackgroundService
{
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<ServerTimeService> _logger;
    private readonly VehicleExpiryNotifier _vehicleExpiry;

    
    public ServerTimeService(PlayerStateManager playerManager, BroadcastService broadcast, IVehicleRepository vehicles,
        ILogger<ServerTimeService> logger)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _logger = logger;
        _vehicleExpiry = new VehicleExpiryNotifier(vehicles, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Server time service started (NfyServerTime 2811 every {Sec}s)",
            ServerTimeCodec.ServerTimeInterval.TotalSeconds);

        
        
        
        
        
        var colosseumOpen = ColosseumSchedule.IsOpen(ServerWallClock.Now());

        using var timer = new PeriodicTimer(ServerTimeCodec.ServerTimeInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            
            
            
            
            
            
            
            var now = ServerWallClock.Now();
            var body = ServerTimeCodec.BuildServerTimeBody(now);
            var nowOle = now.DateTime.ToOADate(); 
            var sent = 0;

            
            
            
            
            
            
            
            
            
            
            
            var openNow = ColosseumSchedule.IsOpen(now);
            byte[]? colosseumBody = null;
            if (openNow != colosseumOpen)
            {
                colosseumOpen = openNow;
                if (ColosseumService.MatchesServed)
                {
                    colosseumBody = ColosseumSchedule.Build2879(openNow);
                    _logger.LogInformation("Colosseum {State} (server clock {Now:HH:mm:ss}); sending 2879 to all players",
                        openNow ? "opened" : "closed", now);
                }
                else
                {
                    _logger.LogDebug("Colosseum schedule {State} (server clock {Now:HH:mm:ss}); no 2879, matches are not served",
                        openNow ? "opened" : "closed", now);
                }
            }

            
            
            var clientListVersion = VehicleClientList.Version;
            var players = _playerManager.GetAllPlayers().ToList();
            foreach (var player in players)
            {
                var session = _broadcast.GetSession(player.SessionId);
                if (session is null)
                    continue;
                try
                {
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyServerTime, body);
                    if (colosseumBody is not null)
                        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyColosseumStart, colosseumBody);
                    sent++;
                }
                catch (Exception ex)
                {
                    
                    _logger.LogDebug(ex, "NfyServerTime send failed for session {Id}",
                        player.SessionId);
                }
            }

            if (sent > 0)
                _logger.LogTrace("Sent NfyServerTime (2811) to {Count} player(s)", sent);

            
            
            
            foreach (var player in players)
            {
                var session = _broadcast.GetSession(player.SessionId);
                if (session is null)
                    continue;
                try
                {
                    await _vehicleExpiry.TickAsync(player, session, nowOle, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    
                    
                    
                    _logger.LogWarning(ex, "OnVehicleListUpdate (2838) tick failed for session {Id}", player.SessionId);
                }
            }
            _vehicleExpiry.Retain(players, clientListVersion);
        }
    }
}
