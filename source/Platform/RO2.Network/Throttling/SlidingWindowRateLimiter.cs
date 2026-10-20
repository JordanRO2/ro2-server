using System.Collections.Concurrent;

namespace RO2.Network.Throttling;

/// <summary>
/// Thread-safe, in-memory sliding-window rate limiter. Per key, at most <c>limit</c> hits are admitted within any window. A refused hit is not counted.
/// </summary>
/// <remarks>
/// Time is passed in as milliseconds of a monotonic clock (<see cref="Environment.TickCount64"/> in production). Each key has its own lock, so distinct keys do not contend. Idle keys are evicted every <see cref="PruneEveryCalls"/> calls, or on demand through <see cref="Prune"/>.
/// </remarks>
public sealed class SlidingWindowRateLimiter<TKey> where TKey : notnull
{
 /// <summary>A sweep of idle keys runs once per this many <c>TryAcquire</c> calls.</summary>
    public const int PruneEveryCalls = 1024;

    private readonly int _limit;
    private readonly long _windowMs;
    private readonly ConcurrentDictionary<TKey, KeyWindow> _windows;
    private long _calls;

 /// <summary>Creates a limiter.</summary>
 /// <param name="limit">Hits admitted per key inside one window (at least 1).</param>
 /// <param name="windowMs">Window length in milliseconds (greater than 0).</param>
 /// <param name="comparer">Key comparer. Null uses the default comparer; pass an ordinal comparer for string keys that must not be culture-sensitive.</param>
    public SlidingWindowRateLimiter(int limit, long windowMs, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowMs, 1L);
        _limit = limit;
        _windowMs = windowMs;
        _windows = new ConcurrentDictionary<TKey, KeyWindow>(comparer);
    }

 /// <summary>Hits admitted per key per window.</summary>
    public int Limit => _limit;

 /// <summary>Window length in milliseconds.</summary>
    public long WindowMs => _windowMs;

 /// <summary>Number of keys currently tracked.</summary>
    public int TrackedKeys => _windows.Count;

 /// <summary>Attempts a hit on the process's monotonic clock.</summary>
    public bool TryAcquire(TKey key) => TryAcquire(key, Environment.TickCount64);

 /// <summary>
 /// Attempts a hit for <paramref name="key"/> at <paramref name="nowMs"/>. Returns true, and counts the hit, when the key is within its limit.
 /// </summary>
    public bool TryAcquire(TKey key, long nowMs)
    {
        if (Interlocked.Increment(ref _calls) % PruneEveryCalls == 0)
            Prune(nowMs);

        while (true)
        {
            var window = _windows.GetOrAdd(key, static _ => new KeyWindow());
            if (window.TryAcquire(nowMs, _limit, _windowMs, out var admitted))
                return admitted;
            
        }
    }

 /// <summary>Forgets <paramref name="key"/>, for example after a successful login clears its failures.</summary>
    public void Reset(TKey key) => _windows.TryRemove(key, out _);

 /// <summary>Evicts every key whose hits have all left the window at <paramref name="nowMs"/>.</summary>
 /// <returns>The number of keys evicted.</returns>
    public int Prune(long nowMs)
    {
        var removed = 0;
        foreach (var pair in _windows)
        {
            if (pair.Value.TryRetire(nowMs, _windowMs) && _windows.TryRemove(pair))
                removed++;
        }

        return removed;
    }

    private sealed class KeyWindow
    {
        private readonly Queue<long> _hits = new();
        private readonly Lock _gate = new();
        private bool _retired;

 /// <summary>
 /// Returns false when this window was retired and the caller must fetch a fresh one. Otherwise <paramref name="admitted"/> carries the decision.
 /// </summary>
        public bool TryAcquire(long nowMs, int limit, long windowMs, out bool admitted)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    admitted = false;
                    return false;
                }

                Expire(nowMs, windowMs);
                if (_hits.Count >= limit)
                {
                    admitted = false;
                    return true;
                }

                _hits.Enqueue(nowMs);
                admitted = true;
                return true;
            }
        }

 /// <summary>Marks this window retired when it holds no live hits. Returns true if it is retired.</summary>
        public bool TryRetire(long nowMs, long windowMs)
        {
            lock (_gate)
            {
                if (_retired)
                    return true;
                Expire(nowMs, windowMs);
                if (_hits.Count > 0)
                    return false;
                _retired = true;
                return true;
            }
        }

        private void Expire(long nowMs, long windowMs)
        {
            while (_hits.Count > 0 && nowMs - _hits.Peek() >= windowMs)
                _hits.Dequeue();
        }
    }
}
