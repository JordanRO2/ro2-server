namespace RO2.Tests.Economy;

/// <summary>
/// The "yield at every repo call" hook of <see cref="EconomyFixture"/>. Every fake repository method calls
/// <see cref="PointAsync"/> on entry, before it touches its store. Outside a controlled run that is a plain
/// <see cref="Task.Yield"/> (so code never gets to assume a repo call completes synchronously). Inside
/// <see cref="RunAsync(IReadOnlyList{Func{Task}}, IReadOnlyList{int}?)"/> each operation is a participant that PARKS at
/// every point and only continues when the driver picks it, so two operations can be interleaved
/// deterministically at each await boundary.
/// </summary>
/// <remarks>
/// A fake's own body (after the point) is atomic, like a single Mongo write; the non-atomic default bodies of
/// <c>IInventoryRepository</c> (read, then update) expose their two calls as two points, which is how a lost
/// update is reproduced. A participant that blocks on something other than a point (a <c>SemaphoreSlim</c> gate
/// another parked participant holds) is detected after <see cref="BlockedAfter"/> and simply not scheduled until
/// the lock is released; if every live participant stays blocked for <see cref="DeadlockAfter"/> the run throws.
/// </remarks>
public sealed class RepoScheduler
{
    private static readonly AsyncLocal<Participant?> Current = new();

    private readonly object _traceGate = new();
    private readonly List<string> _trace = [];

    /// <summary>How long a released participant may run without reaching its next point or finishing before it
    /// is treated as blocked on a lock.</summary>
    public TimeSpan BlockedAfter { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long every live participant may stay blocked before the run is declared deadlocked.</summary>
    public TimeSpan DeadlockAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Every point reached so far, as <c>"P0:Inventory.GetById"</c> (participant index, point name), in
    /// the order they were reached. Outside a run the participant part is <c>"-"</c>.</summary>
    public IReadOnlyList<string> Trace { get { lock (_traceGate) return [.. _trace]; } }

    public void ClearTrace() { lock (_traceGate) _trace.Clear(); }

    /// <summary>Called by the fake repositories at the top of every method.</summary>
    public Task PointAsync(string name)
    {
        var p = Current.Value;
        lock (_traceGate) _trace.Add($"{(p is null ? "-" : "P" + p.Index)}:{name}");
        if (p is null) return YieldAsync();

        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Resume = resume;
        p.Signal.Release();
        return resume.Task;
    }

    private static async Task YieldAsync() => await Task.Yield();

    /// <summary>
    /// Runs <paramref name="ops"/> as participants 0..n-1. <paramref name="schedule"/> lists the participant to
    /// advance at each decision (one decision = run that participant to its next point or to its end); a
    /// finished participant's entry is skipped, and once the list is used up the rest run lowest-index first.
    /// An operation's exception is captured in <see cref="InterleaveResult.Errors"/>, not thrown.
    /// </summary>
    public async Task<InterleaveResult> RunAsync(IReadOnlyList<Func<Task>> ops, IReadOnlyList<int>? schedule = null)
    {
        var (result, _, _) = await DriveAsync(ops, schedule ?? [], recordAlternatives: false);
        return result;
    }

    /// <summary>String form of the schedule: one character per decision, <c>'0'</c> for participant 0, <c>'1'</c>
    /// for participant 1, and so on (<c>"0011"</c>).</summary>
    public Task<InterleaveResult> RunAsync(IReadOnlyList<Func<Task>> ops, string schedule)
        => RunAsync(ops, schedule.Select(c => c - '0').ToList());

    /// <summary>
    /// Runs the operations under EVERY schedule (depth-first over the decision tree), building a fresh world for
    /// each run, until the tree is exhausted or <paramref name="maxRuns"/> is reached. <paramref name="create"/>
    /// makes the fixture, <paramref name="ops"/> the operations over it, <paramref name="verify"/> checks the end
    /// state (throw to fail; the failing schedule is added to the exception). Returns how many runs were made and
    /// whether the whole tree was covered.
    /// </summary>
    public static async Task<ExploreSummary> ExploreAllAsync(
        Func<EconomyFixture> create,
        Func<EconomyFixture, IReadOnlyList<Func<Task>>> ops,
        Func<EconomyFixture, InterleaveResult, Task> verify,
        int maxRuns = 5000)
    {
        var pending = new Stack<int[]>();
        pending.Push([]);
        var runs = 0;
        while (pending.Count > 0)
        {
            if (runs >= maxRuns) return new ExploreSummary(runs, Exhausted: false);
            var prefix = pending.Pop();
            var world = create();
            var (result, chosen, alternatives) = await world.Scheduler.DriveAsync(ops(world), prefix, recordAlternatives: true);

            // Only a run that followed its whole prefix is a fresh path; otherwise a blocked or finished choice was
            // swapped for another one and the path is covered by its twin.
            if (!prefix.Select((c, i) => chosen.Count > i && chosen[i] == c).All(followed => followed)) continue;
            runs++;

            try { await verify(world, result); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Interleaving [{string.Join(",", chosen)}] violated the check ({ex.GetType().Name}: {ex.Message}). "
                    + $"Trace: {string.Join(" ", result.Trace)}", ex);
            }

            for (var i = prefix.Length; i < chosen.Count; i++)
                foreach (var alt in alternatives[i])
                    if (alt != chosen[i])
                        pending.Push([.. chosen.Take(i), alt]);
        }
        return new ExploreSummary(runs, Exhausted: true);
    }

    private async Task<(InterleaveResult Result, List<int> Chosen, List<int[]> Alternatives)> DriveAsync(
        IReadOnlyList<Func<Task>> ops, IReadOnlyList<int> prefix, bool recordAlternatives)
    {
        ClearTrace();
        var parts = new List<Participant>();
        foreach (var op in ops)
        {
            var p = new Participant(parts.Count);
            parts.Add(p);
            // Started on the pool; it runs to its first point (or its end) before the next one is started. One that
            // blocks before reaching a point (a lock held by an earlier participant) stays Running and is picked up
            // by the decision loop once it parks.
            p.Running = true;
            p.Task = Task.Run(async () =>
            {
                Current.Value = p;
                try { await op(); }
                catch (Exception ex) { p.Error = ex; }
                finally { p.Done = true; p.Signal.Release(); }
            });
            if (await p.Signal.WaitAsync(BlockedAfter)) p.Running = false;
        }

        var chosen = new List<int>();
        var alternatives = new List<int[]>();
        var deadline = DateTime.UtcNow + DeadlockAfter;
        while (parts.Any(p => !p.Done))
        {
            var live = parts.Where(p => !p.Done).Select(p => p.Index).ToArray();
            var decision = chosen.Count;
            var wanted = decision < prefix.Count && live.Contains(prefix[decision]) ? prefix[decision] : live[0];
            // Try the wanted participant first, then the others in index order; a blocked one is passed over.
            var progressed = -1;
            foreach (var i in live.OrderBy(i => i == wanted ? -1 : i))
                if (await StepAsync(parts[i])) { progressed = i; break; }
            if (progressed < 0)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Every live operation is blocked outside a repo point (deadlock?). Trace: "
                        + string.Join(" ", Trace));
                continue;
            }
            deadline = DateTime.UtcNow + DeadlockAfter;
            chosen.Add(progressed);
            if (recordAlternatives) alternatives.Add(live);
        }

        await Task.WhenAll(parts.Select(p => p.Task!));
        return (new InterleaveResult(Trace, [.. parts.Select(p => p.Error)]), chosen, alternatives);
    }

    /// <summary>Releases a parked participant (or keeps waiting on one that is still running) and waits for it to
    /// park again or finish. False when it neither parked nor finished within <see cref="BlockedAfter"/>.</summary>
    private async Task<bool> StepAsync(Participant p)
    {
        if (p.Done) return true;
        if (!p.Running)
        {
            var resume = p.Resume!;
            p.Resume = null;
            p.Running = true;
            resume.SetResult();
        }
        if (!await p.Signal.WaitAsync(BlockedAfter)) return false;
        p.Running = false;
        return true;
    }

    private sealed class Participant(int index)
    {
        public int Index { get; } = index;
        public Task? Task;
        public TaskCompletionSource? Resume;
        public bool Running;
        public volatile bool Done;
        public Exception? Error;
        public readonly SemaphoreSlim Signal = new(0);
    }
}

/// <summary>What <see cref="RepoScheduler.RunAsync(IReadOnlyList{Func{Task}}, IReadOnlyList{int}?)"/> observed.</summary>
/// <param name="Trace">Every repo point reached, in order (<c>"P1:Mail.Replace"</c>).</param>
/// <param name="Errors">Per participant, the exception its operation threw, or null.</param>
public sealed record InterleaveResult(IReadOnlyList<string> Trace, IReadOnlyList<Exception?> Errors)
{
    /// <summary>Throws the first captured operation exception, if any.</summary>
    public void ThrowIfFaulted()
    {
        foreach (var e in Errors)
            if (e is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
    }

    /// <summary>True when some operation threw the PlayerState negative-wallet guard (invariant D).</summary>
    public bool HitNegativeWallet => Errors.Any(e => e is ArgumentOutOfRangeException { ParamName: "Gold" });
}

/// <summary>Outcome of <see cref="RepoScheduler.ExploreAllAsync"/>.</summary>
public sealed record ExploreSummary(int Runs, bool Exhausted);
