using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends RMI 2774 NfyDungeonBindReset to every in-world player once a day, at the daily reset instant.
/// </summary>
/// <remarks>
/// <para>
/// The notice is driven by a server timer and has no client request. It is sent to everyone in world at the
/// reset instant and is not sent at login. The body is <c>[u32 DungeonID]</c>, one frame per bound dungeon.
/// </para>
/// <para>
/// The bound dungeons are the DungeonInfo rows with a Bind_Value, in DungeonID order. The same frames go to
/// every player.
/// </para>
/// </remarks>
public class DungeonBindResetService : BackgroundService
{
 /// Daily reset instant, as a UTC time of day.
    private static readonly TimeSpan ResetTimeOfDayUtc = TimeSpan.FromHours(16);

 /// <summary>
 /// Longest single wait. The clock is re-read at least this often, so a clock correction moves the send to the
 /// right instant.
 /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly GameDataStore _gameData;
    private readonly ILogger<DungeonBindResetService> _logger;

    public DungeonBindResetService(PlayerStateManager playerManager, BroadcastService broadcast,
        GameDataStore gameData, ILogger<DungeonBindResetService> logger)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _gameData = gameData;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = NextResetAfter(DateTimeOffset.UtcNow);
        _logger.LogInformation(
            "Dungeon bind reset service started (NfyDungeonBindReset 2774 daily at {Time} UTC, next {Next:u}, bound dungeons [{Ids}])",
            ResetTimeOfDayUtc, next, string.Join(", ", BoundDungeonIds()));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var remaining = next - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < MaxWait ? remaining : MaxWait, stoppingToken);
                    continue;
                }

                await SendResetAsync(stoppingToken);

                
                
                next = NextResetAfter(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            
        }
    }

 /// <summary>The first reset instant strictly after <paramref name="nowUtc"/>.</summary>
    private static DateTimeOffset NextResetAfter(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(utc.Date, TimeSpan.Zero) + ResetTimeOfDayUtc;
        return today > utc ? today : today.AddDays(1);
    }

 /// <summary>DungeonInfo rows with a Bind_Value, in DungeonID order.</summary>
    private List<uint> BoundDungeonIds() =>
        _gameData.DungeonInfo
            .Where(d => !string.IsNullOrWhiteSpace(d.Bind_Value)
                        && !d.Bind_Value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            .Select(d => (uint)d.DungeonID)
            .OrderBy(id => id)
            .ToList();

    private async Task SendResetAsync(CancellationToken ct)
    {
        var ids = BoundDungeonIds();
        if (ids.Count == 0)
        {
            _logger.LogWarning("Dungeon bind reset due but no DungeonInfo row has a Bind_Value; nothing sent");
            return;
        }

        
        var bodies = new List<byte[]>(ids.Count);
        foreach (var id in ids)
        {
            using var pw = new PacketWriter(4);
            pw.WriteUInt32(id);
            bodies.Add(pw.ToArray());
        }

        var sent = 0;
        foreach (var player in _playerManager.GetAllPlayers())
        {
            var session = _broadcast.GetSession(player.SessionId);
            if (session is null)
                continue;
            try
            {
                
                foreach (var body in bodies)
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyDungeonBindReset, body, ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                
                _logger.LogDebug(ex, "NfyDungeonBindReset send failed for session {Id}", player.SessionId);
            }
        }

        _logger.LogInformation("Dungeon bind reset: sent NfyDungeonBindReset (2774) for [{Ids}] to {Count} player(s)",
            string.Join(", ", ids), sent);
    }
}
