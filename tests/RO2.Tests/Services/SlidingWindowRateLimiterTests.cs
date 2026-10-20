using RO2.Network.Throttling;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>Tests for <see cref="SlidingWindowRateLimiter{TKey}"/> with a pinned clock (milliseconds passed in).</summary>
public sealed class SlidingWindowRateLimiterTests
{
    private const int Limit = 4;
    private const long Window = 5000;

    private static SlidingWindowRateLimiter<string> New() => new(Limit, Window, StringComparer.Ordinal);

    [Fact]
    public void NAllowedInWindow_NPlusOneRefused()
    {
        var rl = New();
        for (var i = 0; i < Limit; i++)
            Assert.True(rl.TryAcquire("ip", 1000 + i), $"hit {i}");
        Assert.False(rl.TryAcquire("ip", 1000 + Limit));
    }

    [Fact]
    public void RefusedHitsAreNotCounted_SoRecoveryIsTheOldestHitPlusWindow()
    {
        var rl = New();
        for (var i = 0; i < Limit; i++)
            rl.TryAcquire("ip", 1000L * i); 
        Assert.False(rl.TryAcquire("ip", 4000));
        Assert.False(rl.TryAcquire("ip", 4999));  
        Assert.True(rl.TryAcquire("ip", 5000));   
        Assert.False(rl.TryAcquire("ip", 5001));  
        Assert.True(rl.TryAcquire("ip", 6000));   
    }

    [Fact]
    public void RefillsAfterTheWindow()
    {
        var rl = New();
        for (var i = 0; i < Limit; i++)
            Assert.True(rl.TryAcquire("ip", 100));
        Assert.False(rl.TryAcquire("ip", 100 + Window - 1));
        for (var i = 0; i < Limit; i++)
            Assert.True(rl.TryAcquire("ip", 100 + Window), $"refill {i}");
        Assert.False(rl.TryAcquire("ip", 100 + Window));
    }

    [Fact]
    public void DistinctKeysAreIndependent()
    {
        var rl = New();
        for (var i = 0; i < Limit; i++)
            rl.TryAcquire("a", 0);
        Assert.False(rl.TryAcquire("a", 1));
        Assert.True(rl.TryAcquire("b", 1));
        Assert.True(rl.TryAcquire("c", 1));
    }

    [Fact]
    public void WorksWithNonStringKeys()
    {
        var rl = new SlidingWindowRateLimiter<(uint Requester, uint Target)>(1, 1000);
        Assert.True(rl.TryAcquire((1, 2), 0));
        Assert.False(rl.TryAcquire((1, 2), 10));
        Assert.True(rl.TryAcquire((1, 3), 10));
        Assert.True(rl.TryAcquire((2, 1), 10));
    }

    [Fact]
    public void MatchesChatFloodGateRule_FourLinesInFiveSeconds()
    {
        
        
        var rl = new SlidingWindowRateLimiter<string>(4, 5000);
        long t0 = 14_801, t1 = 15_886, t2 = 17_441, t3 = 18_926, t4 = 19_569, t5 = t4 + 1_674;
        Assert.True(rl.TryAcquire("s", t0));
        Assert.True(rl.TryAcquire("s", t1));
        Assert.True(rl.TryAcquire("s", t2));
        Assert.True(rl.TryAcquire("s", t3));
        Assert.False(rl.TryAcquire("s", t4));
        Assert.True(rl.TryAcquire("s", t5));
    }

    [Fact]
    public void ResetForgetsTheKey()
    {
        var rl = New();
        for (var i = 0; i < Limit; i++)
            rl.TryAcquire("ip", 0);
        Assert.False(rl.TryAcquire("ip", 1));
        rl.Reset("ip");
        Assert.True(rl.TryAcquire("ip", 2));
    }

    [Fact]
    public void PruneEvictsOnlyIdleKeys()
    {
        var rl = New();
        rl.TryAcquire("old", 0);
        rl.TryAcquire("fresh", 4000);
        Assert.Equal(2, rl.TrackedKeys);
        Assert.Equal(1, rl.Prune(Window)); 
        Assert.Equal(1, rl.TrackedKeys);
        Assert.Equal(1, rl.Prune(4000 + Window));
        Assert.Equal(0, rl.TrackedKeys);
        Assert.True(rl.TryAcquire("old", 4000 + Window)); 
    }

    [Fact]
    public void AutomaticSweepBoundsTheTable()
    {
        var rl = new SlidingWindowRateLimiter<int>(1, 1000);
        
        for (var k = 0; k < SlidingWindowRateLimiter<int>.PruneEveryCalls - 1; k++)
            rl.TryAcquire(k, 0);
        Assert.Equal(SlidingWindowRateLimiter<int>.PruneEveryCalls - 1, rl.TrackedKeys);
        rl.TryAcquire(-1, 1_000_000); 
        Assert.Equal(1, rl.TrackedKeys);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 0)]
    [InlineData(-1, 1000)]
    public void RejectsNonsenseConfiguration(int limit, long window)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter<string>(limit, window));

    [Fact]
    public async Task ConcurrentTryAcquire_AdmitsExactlyTheLimit_OnOneKey()
    {
        var rl = new SlidingWindowRateLimiter<string>(25, 60_000);
        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                if (rl.TryAcquire("hot", 1000)) 
                    Interlocked.Increment(ref admitted);
        })));
        Assert.Equal(25, admitted);
    }

    [Fact]
    public async Task ConcurrentTryAcquire_WithPruning_NeverOverAdmits()
    {
        
        
        var rl = new SlidingWindowRateLimiter<string>(10, 60_000);
        var admitted = 0;
        using var stop = new CancellationTokenSource();
        var pruner = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                rl.Prune(1500);
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                if (rl.TryAcquire("k", 1000))
                    Interlocked.Increment(ref admitted);
        })));
        await stop.CancelAsync();
        await pruner;

        Assert.Equal(10, admitted);
    }

    [Fact]
    public async Task ConcurrentDistinctKeys_EachGetExactlyTheLimit()
    {
        var rl = new SlidingWindowRateLimiter<int>(3, 60_000);
        var perKey = new int[50];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var k = 0; k < perKey.Length; k++)
                for (var i = 0; i < 10; i++)
                    if (rl.TryAcquire(k, 5))
                        Interlocked.Increment(ref perKey[k]);
        })));
        Assert.All(perKey, n => Assert.Equal(3, n));
    }
}
