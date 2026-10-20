using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// RegenService must never take the Zone host down. An exception that escapes a BackgroundService faults it, and
/// the default BackgroundServiceExceptionBehavior.StopHost then stops the whole host. The per-player 2401 send was
/// unguarded, so one broken socket or a send lock disposed by a concurrent disconnect stopped regeneration for everyone.
/// </summary>
/// <remarks>
/// The real send needs a connected ProudSession, so the tests drive the two seams the service runs through in
/// production: <c>RegenTickAsync</c> (the per-player step, with the send passed in) and <c>RunAsync</c> (the
/// delay-then-tick loop, with the tick and interval passed in).
/// </remarks>
public class RegenServiceResilienceTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static (RegenService Service, RecordingLogger<RegenService> Log) NewService()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var log = new RecordingLogger<RegenService>();
        var service = new RegenService(players, new BroadcastService(players, NullLogger<BroadcastService>.Instance), log);
        return (service, log);
    }

    private static PlayerState Hurt(uint sessionId) => new()
    {
        SessionId = sessionId, EntityId = PlayerStateManager.BuildActorHandle(sessionId),
        Hp = 5000, MaxHp = 10000, Mp = 100, MaxMp = 5000,
    };

 /// <summary>
 /// The original failure: the first player's send throws the way a dying session does (a half-closed socket, then a
 /// send lock disposed during the send). The exception does not escape, the next player still gets its 2401, and
 /// each failure is logged against its own session.
 /// </summary>
    [Fact]
    public async Task FailingSends_DoNotStopRegenForTheOtherPlayers()
    {
        var (service, log) = NewService();
        var halfClosed = Hurt(1);
        var disposed = Hurt(2);
        var healthy = Hurt(3);
        var sent = new List<(uint Session, int Hp, int Mp)>();

        var failed = await service.RegenTickAsync([halfClosed, disposed, healthy], player =>
        {
            if (player.SessionId == 1) throw new SocketException((int)SocketError.ConnectionReset);
            if (player.SessionId == 2) throw new ObjectDisposedException(nameof(SemaphoreSlim));
            sent.Add((player.SessionId, player.Hp, player.Mp));
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(2, failed);
        Assert.Equal((3u, 5030, 112), Assert.Single(sent));

        
        
        Assert.Equal((5030, 112), (halfClosed.Hp, halfClosed.Mp));
        Assert.Equal((5030, 112), (disposed.Hp, disposed.Mp));

        var failures = log.Entries.Where(e => e.Exception is not null).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Contains("session 1", failures[0].Message);
        Assert.IsType<SocketException>(failures[0].Exception);
        Assert.Contains("session 2", failures[1].Message);
        Assert.IsType<ObjectDisposedException>(failures[1].Exception);
    }

 /// <summary>A send that faults asynchronously, after its first await, is handled the same way as one that throws before returning its task.</summary>
    [Fact]
    public async Task AsynchronouslyFaultingSend_IsCaughtPerPlayer()
    {
        var (service, _) = NewService();
        var sent = new List<uint>();

        var failed = await service.RegenTickAsync([Hurt(1), Hurt(2)], async player =>
        {
            await Task.Yield();
            if (player.SessionId == 1) throw new IOException("Unable to write data to the transport connection.");
            sent.Add(player.SessionId);
        }, CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal(2u, Assert.Single(sent));
    }

 /// <summary>
 /// Pins the regeneration math so refactoring cannot change it: about 0.3% of max HP and 0.24% of max SP per tick,
 /// truncated, with a minimum of 1 and clamped at the maximum. No regeneration while dead (HP 0), for either bar.
 /// A 2401 is sent only when a bar changed.
 /// </summary>
    [Theory]
    [InlineData(5000, 10000, 100, 5000, 5030, 112, true)]   
    [InlineData(50, 100, 99, 100, 51, 100, true)]           
    [InlineData(9990, 10000, 5000, 5000, 10000, 5000, true)] 
    [InlineData(1234, 8000, 10, 1234, 1258, 12, true)]      
    [InlineData(0, 10000, 10, 5000, 0, 10, false)]          
    [InlineData(10000, 10000, 5000, 5000, 10000, 5000, false)] 
    public void ApplyRegen_MathIsUnchanged(int hp, int maxHp, int mp, int maxMp, int hpAfter, int mpAfter, bool changed)
    {
        var player = new PlayerState { SessionId = 1, Hp = hp, MaxHp = maxHp, Mp = mp, MaxMp = maxMp };

        Assert.Equal(changed, RegenService.ApplyRegen(player));
        Assert.Equal((hpAfter, mpAfter), (player.Hp, player.Mp));
    }

 /// <summary>Only players whose bars changed are passed to the send.</summary>
    [Fact]
    public async Task OnlyPlayersWhoseBarsMoved_AreSent()
    {
        var (service, _) = NewService();
        var dead = new PlayerState { SessionId = 1, Hp = 0, MaxHp = 100, Mp = 0, MaxMp = 100 };
        var full = new PlayerState { SessionId = 2, Hp = 100, MaxHp = 100, Mp = 100, MaxMp = 100 };
        var sent = new List<uint>();

        var failed = await service.RegenTickAsync([dead, full, Hurt(3)], player =>
        {
            sent.Add(player.SessionId);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(3u, Assert.Single(sent));
    }

 /// <summary>
 /// A tick that throws an unexpected exception is logged as an error, and the loop continues with the next tick
 /// instead of faulting the BackgroundService.
 /// </summary>
    [Fact]
    public async Task RunAsync_ATickThatThrows_DoesNotEndTheLoop()
    {
        var (service, log) = NewService();
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        await service.RunAsync(1, _ =>
        {
            if (++ticks == 1) throw new InvalidOperationException("Collection was modified");
            stop.Cancel();   
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal(2, ticks);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

 /// <summary>
 /// An OperationCanceledException not caused by host shutdown (a timeout inside a send, for example) counts as a
 /// failed tick, not as a reason to stop regeneration.
 /// </summary>
    [Fact]
    public async Task RunAsync_ACancellationThatIsNotAStop_IsSurvived()
    {
        var (service, log) = NewService();
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        await service.RunAsync(1, _ =>
        {
            if (++ticks == 1) throw new TaskCanceledException("send timed out");
            stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Equal(2, ticks);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

 /// <summary>
 /// Stopping during the wait ends the loop through the delay's cancellation, which the host treats as a normal
 /// stop. No tick runs and nothing is logged as an error.
 /// </summary>
    [Fact]
    public async Task RunAsync_StopDuringTheWait_EndsTheLoopAsCanceled()
    {
        var (service, log) = NewService();
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        var loop = service.RunAsync(60_000, _ => { ticks++; return Task.CompletedTask; }, stop.Token);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.True(loop.IsCanceled);
        Assert.Equal(0, ticks);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

 /// <summary>
 /// A stop that arrives while a tick is running is not swallowed by the per-tick guard: the cancellation
 /// propagates and the loop ends.
 /// </summary>
    [Fact]
    public async Task RunAsync_StopInsideATick_IsNotSwallowed()
    {
        var (service, log) = NewService();
        using var stop = new CancellationTokenSource();

        var loop = service.RunAsync(1, ct =>
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, stop.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

 /// <summary>
 /// The same applies to the per-player guard: a stop raised from a send ends the tick instead of counting as
 /// that player's failure, and no later player is sent.
 /// </summary>
    [Fact]
    public async Task RegenTickAsync_StopInsideASend_IsNotSwallowed()
    {
        var (service, _) = NewService();
        using var stop = new CancellationTokenSource();
        var sent = new List<uint>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegenTickAsync([Hurt(1), Hurt(2)], player =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            sent.Add(player.SessionId);
            return Task.CompletedTask;
        }, stop.Token));

        Assert.Empty(sent);
    }

 /// <summary>
 /// End to end through the hosted-service entry point: starting and then stopping completes ExecuteAsync without
 /// a fault, so StopHost does not treat a normal shutdown as a crash.
 /// </summary>
    [Fact]
    public async Task StartThenStop_CompletesWithoutAFault()
    {
        var (service, log) = NewService();

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var execute = service.ExecuteTask;
        Assert.NotNull(execute);
        Assert.True(execute.IsCompleted);
        Assert.False(execute.IsFaulted);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }
}
