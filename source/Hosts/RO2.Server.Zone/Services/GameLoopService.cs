using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Server.Zone.Configuration;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The server's central game loop: a fixed-timestep simulation clock running on a dedicated thread. Every
/// subsystem that needs ticking registers as an <see cref="ITickable"/> and is advanced from here.
/// </summary>
/// <remarks>
/// The step rate adapts to activity. It idles at <see cref="TickConfig.MinHz"/> when no player is online and
/// runs at <see cref="TickConfig.MaxHz"/> otherwise. Under sustained overload the rate drops toward the minimum.
/// Durable writes are not made at tick rate; see <see cref="PersistenceService"/>.
/// </remarks>
public sealed class GameLoopService : BackgroundService
{
    private readonly ServerConfiguration _config;
    private readonly PlayerStateManager _players;
    private readonly IEnumerable<ITickable> _tickables;
    private readonly ILogger<GameLoopService> _logger;

    private TickConfig Cfg => _config.Tick;

    private volatile int _currentHz;
    private Thread? _thread;

 /// <summary>Current simulation rate in Hz (floats between Min and Max).</summary>
    public int CurrentHz => _currentHz;

 /// <summary>Ticks per second over the last telemetry window.</summary>
    public double CurrentTps { get; private set; }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    public GameLoopService(ServerConfiguration config, PlayerStateManager players,
        IEnumerable<ITickable> tickables, ILogger<GameLoopService> logger)
    {
        _config = config;
        _players = players;
        _tickables = tickables;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        
        
        _thread = new Thread(() => RunLoop(stoppingToken))
        {
            Name = "GameLoop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void RunLoop(CancellationToken ct)
    {
        var tickables = _tickables.ToArray();
        bool hiRes = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;

        int minHz = Math.Clamp(Cfg.MinHz, 1, 1000);
        int maxHz = Math.Clamp(Cfg.MaxHz, minHz, 1000);
        int maxSteps = Math.Max(1, Cfg.MaxCatchUpSteps);

        _currentHz = Cfg.Adaptive ? minHz : maxHz;

        _logger.LogInformation(
            "Game loop started on a dedicated thread — variable rate {Min}-{Max} Hz, {Mode}, hi-res timer {HiRes}, {Count} tickable(s): {Names}",
            minHz, maxHz, Cfg.Adaptive ? "adaptive" : "fixed at max",
            hiRes ? "on" : "off", tickables.Length,
            tickables.Length == 0 ? "(none)" : string.Join(", ", tickables.Select(t => t.Name)));

        try
        {
            var sw = Stopwatch.StartNew();
            double last = sw.Elapsed.TotalSeconds;
            double accumulator = 0;

            
            int ticksThisWindow = 0;
            double windowStart = last;
            int overloadStreak = 0;
            int lastLoggedHz = _currentHz;

            while (!ct.IsCancellationRequested)
            {
                double now = sw.Elapsed.TotalSeconds;
                double frame = now - last;
                last = now;

                
                
                if (frame > 0.25) frame = 0.25;
                accumulator += frame;

                double fixedDt = 1.0 / _currentHz;

                int steps = 0;
                while (accumulator >= fixedDt && steps < maxSteps)
                {
                    foreach (var t in tickables)
                    {
                        try { t.Tick(fixedDt); }
                        catch (Exception ex) { _logger.LogError(ex, "Tickable {Name} threw", t.Name); }
                    }
                    accumulator -= fixedDt;
                    steps++;
                    ticksThisWindow++;
                }

                
                
                bool overloaded = false;
                if (accumulator >= fixedDt)
                {
                    overloaded = true;
                    accumulator = 0;
                }

                
                double windowLen = now - windowStart;
                if (windowLen >= 1.0)
                {
                    CurrentTps = ticksThisWindow / windowLen;
                    AdjustRate(overloaded, ref overloadStreak, minHz, maxHz);
                    ticksThisWindow = 0;
                    windowStart = now;

                    if (_currentHz != lastLoggedHz)
                    {
                        _logger.LogInformation("Game loop rate {Old} -> {New} Hz ({Tps:F0} TPS, {Players} online)",
                            lastLoggedHz, _currentHz, CurrentTps, PlayerCount());
                        lastLoggedHz = _currentHz;
                    }
                }

                
                
                double deadline = now + (fixedDt - accumulator);
                SleepUntil(sw, deadline, hiRes, ct);
            }
        }
        finally
        {
            if (hiRes) TimeEndPeriod(1);
            _logger.LogInformation("Game loop stopped");
        }
    }

 /// <summary>
 /// Selects the next rate: the ceiling while players are online, the floor otherwise. Sustained overload
 /// lowers the rate toward the floor.
 /// </summary>
    private void AdjustRate(bool overloaded, ref int overloadStreak, int minHz, int maxHz)
    {
        if (!Cfg.Adaptive)
        {
            _currentHz = maxHz;
            return;
        }

        int desired = PlayerCount() == 0 ? minHz : maxHz;

        
        if (overloaded) overloadStreak = Math.Min(overloadStreak + 1, 4);
        else if (overloadStreak > 0) overloadStreak--;

        if (overloadStreak >= 2)
        {
            desired = Math.Max(minHz, _currentHz / 2);
            _logger.LogWarning(
                "Game loop overloaded at {Hz} Hz ({Tps:F0} TPS) — dropping to {Desired} Hz",
                _currentHz, CurrentTps, desired);
        }

        _currentHz = Math.Clamp(desired, minHz, maxHz);
    }

    private int PlayerCount()
    {
        int n = 0;
        foreach (var p in _players.GetAllPlayers())
            if (p.CharacterId != 0) n++;
        return n;
    }

 /// <summary>
 /// Waits until <paramref name="deadlineSec"/>: coarse sleeps down to a small margin, then a short spin.
 /// </summary>
    private static void SleepUntil(Stopwatch sw, double deadlineSec, bool hiRes, CancellationToken ct)
    {
        const double spinMargin = 0.0015; 
        while (!ct.IsCancellationRequested)
        {
            double remaining = deadlineSec - sw.Elapsed.TotalSeconds;
            if (remaining <= 0) return;
            if (remaining > spinMargin)
            {
                
                
                int ms = hiRes ? 1 : (int)Math.Max(1, (remaining - spinMargin) * 1000);
                Thread.Sleep(ms);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }
}
