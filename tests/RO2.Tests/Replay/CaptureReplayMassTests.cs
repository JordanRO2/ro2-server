using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RO2.Network.Protocol;
using RO2.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Replay;

/// <summary>
/// A [Fact] that is SKIPPED (never failed) when the split-session folder (<c>RO2_CAPTURE_SESSIONS_DIR</c>, default is the server repo's
/// <c>captures\sessions</c>) or its <c>sessions_manifest.json</c> is absent, or there is no Docker daemon for the throwaway mongo:7.
/// </summary>
public sealed class MassReplayFactAttribute : FactAttribute
{
    public MassReplayFactAttribute()
    {
        if (!File.Exists(MassSessions.ManifestPath))
            Skip = $"no sessions_manifest.json under {MassSessions.Dir} (set RO2_CAPTURE_SESSIONS_DIR)";
        else if (new DockerFactAttribute().Skip is { } noDocker)
            Skip = noDocker;
    }
}

internal static class MassSessions
{
    // No built-in default: set RO2_CAPTURE_SESSIONS_DIR to the per-session split folder. Empty so the mass test skips
    // cleanly when no corpus is present (e.g. CI).
    public const string DefaultDir = "";

    public static string Dir => Environment.GetEnvironmentVariable("RO2_CAPTURE_SESSIONS_DIR") is { Length: > 0 } d ? d : DefaultDir;
    public static string ManifestPath => Path.Combine(Dir, "sessions_manifest.json");

    public sealed record Entry(string Session, int Frames);

    public static List<Entry> ReadManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("sessions").EnumerateArray()
            .Where(e => e.GetProperty("world_entry").GetBoolean())
            .Select(e => new Entry(e.GetProperty("session").GetString()!, e.GetProperty("frames").GetInt32()))
            .ToList();
    }

    /// <summary>
    /// <paramref name="count"/> sessions spread evenly over the frame-count ranking of those with enough frames to reach the world and not
    /// so many that one session dominates the run: a mix of short and long sessions, the same pick every run. A count of 0 or less means all.
    /// </summary>
    public static List<Entry> Pick(IEnumerable<Entry> manifest, int count, int minFrames, int maxFrames)
    {
        var pool = manifest.Where(e => e.Frames >= minFrames && e.Frames <= maxFrames)
            .OrderBy(e => e.Frames).ThenBy(e => e.Session, StringComparer.Ordinal).ToList();
        if (count <= 0 || count >= pool.Count) return pool;
        return Enumerable.Range(0, count).Select(i => pool[(int)((i + 0.5) * pool.Count / count)]).ToList();
    }

    public static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}

/// <summary>What one opcode did across all replayed sessions. "Sess" counts sessions, the rest count window-level occurrences.</summary>
internal sealed class OpAgg
{
    public int SessSeen, SessExpected, SessMissed, SessExtra, SessBoth, SessCompared, SessDiff;
    public long Expected, Missed, Matched, Extra, Compared, Diff;
}

/// <summary>Folds per-session window results into per-opcode frequencies, and into overall Tier A/B totals.</summary>
internal sealed class MassAggregate
{
    public int Sessions { get; private set; }
    public Dictionary<ushort, OpAgg> Ops { get; } = [];
    public ConformanceReport.Totals WorldEntry { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public ConformanceReport.Totals Whole { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public List<double> SessionRecall { get; } = [];
    public List<double> SessionPrecision { get; } = [];
    public Dictionary<string, int> Warnings { get; } = [];

    private static ConformanceReport.Totals Add(ConformanceReport.Totals a, ConformanceReport.Totals b)
        => new(a.Windows + b.Windows, a.Expected + b.Expected, a.Ours + b.Ours, a.Matched + b.Matched, a.Lcs + b.Lcs, a.Denominator + b.Denominator);

    public void Add(IReadOnlyList<WindowResult> results, IReadOnlyList<ConformanceReport.FrameDiff> diffs, int worldEntryWindows)
    {
        Sessions++;
        var whole = ConformanceReport.Total(results);
        WorldEntry = Add(WorldEntry, ConformanceReport.Total(results.Take(worldEntryWindows)));
        Whole = Add(Whole, whole);
        SessionRecall.Add(whole.Recall);
        SessionPrecision.Add(whole.Precision);

        var local = new Dictionary<ushort, OpAgg>();
        OpAgg L(ushort op) => local.TryGetValue(op, out var a) ? a : local[op] = new OpAgg();
        foreach (var w in results)
        {
            foreach (var op in w.Expected) L(op).Expected++;
            foreach (var op in w.Matched) L(op).Matched++;
            foreach (var op in w.Missed) L(op).Missed++;
            foreach (var op in w.Extra) L(op).Extra++;
            foreach (var m in w.Warnings)
            {
                // Digits vary per run (ids, counts); the first line of the text is the cause.
                var key = System.Text.RegularExpressions.Regex.Replace(m.Split('\n')[0], @"\d+", "#");
                key = key.Length > 140 ? key[..140] : key;
                Warnings[key] = Warnings.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var d in diffs) { var a = L(d.Op); a.Compared++; if (!d.Identical) a.Diff++; }

        foreach (var (op, s) in local)
        {
            if (!Ops.TryGetValue(op, out var g)) Ops[op] = g = new OpAgg();
            g.SessSeen++;
            if (s.Expected > 0) g.SessExpected++;
            if (s.Missed > 0) g.SessMissed++;
            if (s.Extra > 0) g.SessExtra++;
            if (s.Missed > 0 && s.Extra > 0) g.SessBoth++;
            if (s.Compared > 0) g.SessCompared++;
            if (s.Diff > 0) g.SessDiff++;
            g.Expected += s.Expected; g.Missed += s.Missed; g.Matched += s.Matched; g.Extra += s.Extra; g.Compared += s.Compared; g.Diff += s.Diff;
        }
    }

    /// <summary>A divergence seen in at least this share of the sessions it could have shown up in is "consistent".</summary>
    public const double ConsistentRate = 0.5;

    /// <summary>...and in at least this many sessions (5% of the run, never fewer than 3): a rate over two sessions is not a signal.</summary>
    public int MinSupport => Math.Max(3, Sessions / 20);

    private static readonly IReadOnlyDictionary<ushort, string> S2CNames = typeof(ZoneS2COpcodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
        .GroupBy(f => (ushort)f.GetRawConstantValue()!)
        .ToDictionary(g => g.Key, g => g.First().Name);

    private static string Name(ushort op) => S2CNames.GetValueOrDefault(op, "?");

    private void Section(StringBuilder sb, string title, string columns, Func<KeyValuePair<ushort, OpAgg>, (int Sess, int Applicable, string Detail)> pick, int topConsistent = 25, int topSporadic = 15)
    {
        var rows = Ops.Select(kv => (kv.Key, P: pick(kv))).Where(r => r.P.Sess > 0).ToList();
        bool Consistent((ushort Key, (int Sess, int Applicable, string Detail) P) r)
            => r.P.Sess >= MinSupport && (double)r.P.Sess / r.P.Applicable >= ConsistentRate;
        var consistent = rows.Where(Consistent).OrderByDescending(r => r.P.Sess).ThenBy(r => r.Key).ToList();
        var sporadic = rows.Where(r => !Consistent(r)).OrderByDescending(r => r.P.Sess).ThenBy(r => r.Key).ToList();

        sb.AppendLine($"--- {title} ---");
        sb.AppendLine($"  CONSISTENT (>= {ConsistentRate:P0} of the sessions it applies to, in >= {MinSupport} sessions): {consistent.Count} opcode(s)");
        sb.AppendLine($"  {"op",-5} {"name",-30} {columns}");
        foreach (var r in consistent.Take(topConsistent))
            sb.AppendLine($"  {r.Key,-5} {Name(r.Key),-30} {r.P.Sess,4}/{r.P.Applicable,-4} {(double)r.P.Sess / r.P.Applicable,6:P0}  {r.P.Detail}");
        if (consistent.Count > topConsistent) sb.AppendLine($"  ... {consistent.Count - topConsistent} more consistent");
        sb.AppendLine($"  SPORADIC (ambient / window-attribution noise): {sporadic.Count} opcode(s), top {Math.Min(topSporadic, sporadic.Count)} by sessions affected");
        foreach (var r in sporadic.Take(topSporadic))
            sb.AppendLine($"  {r.Key,-5} {Name(r.Key),-30} {r.P.Sess,4}/{r.P.Applicable,-4} {(double)r.P.Sess / r.P.Applicable,6:P0}  {r.P.Detail}");
        sb.AppendLine();
    }

    public string Format(string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Mass capture-replay conformance (aggregated across sessions) ===");
        sb.AppendLine(header);
        sb.AppendLine();
        string T(string label, ConformanceReport.Totals t) => $"{label}: {t.Windows} windows | Tier A precision {t.Precision:P1} ({t.Matched}/{t.Ours}), recall {t.Recall:P1} ({t.Matched}/{t.Expected}) | Tier B order {t.Order:P1} (LCS {t.Lcs}/{t.Denominator})";
        sb.AppendLine("Pooled over every window of every session:");
        sb.AppendLine("  " + T("World entry (up to the first 1304)", WorldEntry));
        sb.AppendLine("  " + T("Whole replayed span", Whole));
        if (SessionRecall.Count > 0)
        {
            double Median(List<double> v) { var s = v.Order().ToList(); return s[s.Count / 2]; }
            sb.AppendLine($"Per session, whole span: median Tier A precision {Median(SessionPrecision):P1}, median recall {Median(SessionRecall):P1} (mean {SessionPrecision.Average():P1} / {SessionRecall.Average():P1})");
        }
        sb.AppendLine("Rates are 'sessions affected / sessions the opcode applies to' (applies = retail sent it for a MISS, either side sent it for an EXTRA, a matched pair existed for a BYTE-DIFF).");
        sb.AppendLine();

        Section(sb, "MISS: retail sent this reactive opcode in a window and we did not", "sess-missed/sess-retail-sent  rate  occurrences-missed/expected; sessions that ALSO had it extra (moved to another window, not absent)",
            kv => (kv.Value.SessMissed, kv.Value.SessExpected, $"{kv.Value.Missed}/{kv.Value.Expected} occ; also extra in {kv.Value.SessBoth} sess"));
        Section(sb, "EXTRA: we sent this opcode in a window where retail did not", "sess-extra/sess-seen  rate  occurrences extra / ours; sessions that ALSO had it missed (moved to another window)",
            kv => (kv.Value.SessExtra, kv.Value.SessSeen, $"{kv.Value.Extra}/{kv.Value.Extra + kv.Value.Matched} of ours; also missed in {kv.Value.SessBoth} sess"));
        Section(sb, "BYTE-DIFF: matched opcode whose body differs after the identity and clock masks", "sess-differing/sess-compared  rate  frames differing/compared",
            kv => (kv.Value.SessDiff, kv.Value.SessCompared, $"{kv.Value.Diff}/{kv.Value.Compared} frames"));

        if (Warnings.Count > 0)
        {
            sb.AppendLine("--- Server warnings seen while replaying (digits normalised), by occurrences ---");
            foreach (var (text, n) in Warnings.OrderByDescending(kv => kv.Value).Take(10)) sb.AppendLine($"  {n,6}x {text}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Capture-replay conformance at corpus scale: many split retail sessions go through ONE booted Zone container (a per-session reset in
/// between) and the divergences are aggregated per S2C opcode by how many sessions show them, so a gap that appears in most sessions
/// (a real reactive gap) separates from one that appears sporadically (ambient traffic or a window-attribution artifact). A measurement,
/// not a regression gate: the report is the output (<c>--logger "console;verbosity=detailed"</c>).
/// </summary>
/// <remarks>
/// Knobs (environment): <c>RO2_CAPTURE_SESSIONS_DIR</c> (folder with sessions_manifest.json); <c>RO2_MASS_SESSIONS</c> sample size, default 200,
/// 0 = every world-entry session; <c>RO2_MASS_MAX_WINDOWS</c> window groups replayed per session, default 80; <c>RO2_MASS_IDLE_MS</c>
/// quiet time that ends a drain, default 30; <c>RO2_MASS_MIN_FRAMES</c> / <c>RO2_MASS_MAX_FRAMES</c> manifest frame bounds, default 200 / 40000;
/// <c>RO2_MASS_LOG</c> a file that gets one progress line per session.
/// </remarks>
[Collection(GameDataStoreLoadedCollection.Name)] // boots the Zone container + GameDataStore.LoadAsync (a process-wide Loaded setter); must not run parallel with other Loaded setters
public sealed class CaptureReplayMassTests(ITestOutputHelper output, ReplayMongoFixture mongo) : IClassFixture<ReplayMongoFixture>
{
    [MassReplayFact]
    public async Task Many_retail_sessions_replay_on_one_zone_container_and_aggregate()
    {
        var connectionString = await mongo.ConnectionStringAsync();
        if (connectionString is null)
        {
            output.WriteLine("SKIPPED: mongo:7 container did not start: " + mongo.StartupError);
            return;
        }

        var sample = MassSessions.Pick(MassSessions.ReadManifest(MassSessions.ManifestPath),
            MassSessions.EnvInt("RO2_MASS_SESSIONS", 200), MassSessions.EnvInt("RO2_MASS_MIN_FRAMES", 200), MassSessions.EnvInt("RO2_MASS_MAX_FRAMES", 40_000));
        var maxGroups = MassSessions.EnvInt("RO2_MASS_MAX_WINDOWS", 80);
        var idle = TimeSpan.FromMilliseconds(MassSessions.EnvInt("RO2_MASS_IDLE_MS", 30));
        var logPath = Environment.GetEnvironmentVariable("RO2_MASS_LOG");
        void Progress(string line) { if (logPath is { Length: > 0 }) File.AppendAllText(logPath, line + Environment.NewLine); }

        var clock = Stopwatch.StartNew();
        await using var host = await ReplayHost.BootAsync(connectionString, idle);
        var boot = clock.Elapsed;

        var aggregate = new MassAggregate();
        var skipped = new Dictionary<string, int>();
        var windowCount = 0;
        List<string>? witnessSignature = null;
        MassSessions.Entry? witnessEntry = null;
        foreach (var (entry, i) in sample.Select((e, i) => (e, i)))
        {
            var started = clock.Elapsed;
            try
            {
                var run = await ReplayOneAsync(host, entry, maxGroups);
                if (run is null) { Bump(skipped, "no connection with a 1303 and a parseable retail 2307+2310"); continue; }
                if (run.LoginFailed) { Bump(skipped, "LoginPC did not find the seeded character"); continue; }
                aggregate.Add(run.Results, run.Diffs, run.WorldEntryWindows);
                windowCount += run.Results.Count;
                if (witnessSignature is null || run.Results.Count > witnessSignature.Count) { witnessSignature = Signature(run.Results); witnessEntry = entry; }
                Progress($"{i + 1}/{sample.Count} {entry.Session} ok {run.Results.Count} windows in {(clock.Elapsed - started).TotalSeconds:F1}s (load {run.Load.TotalSeconds:F1}, seed {run.Seed.TotalSeconds:F1}, replay {run.Replay.TotalSeconds:F1})");
            }
            catch (Exception ex)
            {
                var why = ex.GetType().Name + ": " + (ex.Message.Length > 100 ? ex.Message[..100] : ex.Message);
                Bump(skipped, why);
                Progress($"{i + 1}/{sample.Count} {entry.Session} FAILED {why}");
            }
        }
        var replayElapsed = clock.Elapsed - boot;

        // Reuse check: the session with the most replayed windows again, after every other one went through the same container. Same opcode lists = the resets hold.
        var drift = "no session replayed";
        if (witnessEntry is not null && witnessSignature is not null)
        {
            var again = await ReplayOneAsync(host, witnessEntry, maxGroups);
            var second = again is null or { LoginFailed: true } ? null : Signature(again.Results);
            drift = second is null ? "re-replay failed"
                : second.SequenceEqual(witnessSignature) ? $"none, {witnessEntry.Session} replayed again gave the identical {second.Count} windows"
                : $"{second.Zip(witnessSignature).Count(p => p.First != p.Second) + Math.Abs(second.Count - witnessSignature.Count)} of {witnessSignature.Count} windows of {witnessEntry.Session} changed when replayed again: "
                    + string.Join(" | ", second.Zip(witnessSignature).Select((p, w) => (p, w)).Where(x => x.p.First != x.p.Second).Take(4).Select(x => $"#{x.w} first [{x.p.Second}] again [{x.p.First}]"));
        }

        var header = new StringBuilder()
            .AppendLine($"Sample: {sample.Count} sessions picked (RO2_MASS_SESSIONS={MassSessions.EnvInt("RO2_MASS_SESSIONS", 200)}, frames {MassSessions.EnvInt("RO2_MASS_MIN_FRAMES", 200)}..{MassSessions.EnvInt("RO2_MASS_MAX_FRAMES", 40_000)}, window groups per session <= {maxGroups}, idle {idle.TotalMilliseconds:F0} ms)")
            .AppendLine($"Replayed: {aggregate.Sessions} sessions, {windowCount} window groups; skipped/failed: {sample.Count - aggregate.Sessions}{(skipped.Count == 0 ? "" : " (" + string.Join("; ", skipped.Select(kv => $"{kv.Value}x {kv.Key}")) + ")")}")
            .AppendLine($"Time: boot {boot.TotalSeconds:F0} s, replay {replayElapsed.TotalSeconds:F0} s ({(aggregate.Sessions == 0 ? 0 : replayElapsed.TotalSeconds / aggregate.Sessions):F1} s/session) on one container")
            .Append($"Container reuse drift: {drift}")
            .ToString();
        output.WriteLine(aggregate.Format(header));
        Assert.True(aggregate.Sessions > 0, "no session could be replayed: " + string.Join("; ", skipped.Keys));
    }

    private static void Bump(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    /// <summary>The opcodes we answered in each window group: what has to come out the same when a session is replayed twice.</summary>
    private static List<string> Signature(IEnumerable<WindowResult> results) => results.Select(r => ConformanceReport.Rle(r.Ours, int.MaxValue)).ToList();

    private sealed record Run(List<WindowResult> Results, int WorldEntryWindows, List<ConformanceReport.FrameDiff> Diffs, TimeSpan Load, TimeSpan Seed, TimeSpan Replay)
    {
        /// <summary>LoginPC could not find the seeded character, so every window after it answered a world that never loaded; such a session says nothing about the server.</summary>
        public bool LoginFailed => Results.Any(r => r.Warnings.Any(w => w.Contains("LoginPCHandler", StringComparison.Ordinal) && w.Contains("not found or wrong account", StringComparison.Ordinal)));
    }

    private static async Task<Run?> ReplayOneAsync(ReplayHost host, MassSessions.Entry entry, int maxGroups)
    {
        var t0 = Stopwatch.StartNew();
        var (connections, _) = CaptureLoader.Load(Path.Combine(MassSessions.Dir, $"RO2Mods_Capture_{entry.Session}.jsonl"));
        foreach (var connection in connections)
        {
            var auth = connection.Frames.FirstOrDefault(f => f.IsC2S && f.Op == CaptureLoader.ZoneAuthOp && f.Body.Length >= 26);
            var own = connection.Frames.FirstOrDefault(f => !f.IsC2S && f.Op == 2310);
            if (auth is null || own is not { Body.Length: >= 16 } || RetailSeed.From(connection.Frames) is not { } seed) continue;

            var ambient = ConformanceReport.AmbientSet(connection.Frames);
            var windows = ConformanceReport.BuildWindows(connection.Frames, ambient);
            var load = t0.Elapsed;
            await using var rig = await CaptureReplayRig.BeginAsync(host, new Guid(auth.Body.AsSpan(10, 16)), seed);
            var seeded = t0.Elapsed - load;
            var retailUid = BitConverter.ToUInt64(own.Body, 4);
            var retailHandle = BitConverter.ToUInt32(own.Body, 12);
            rig.SetRetailIdentity(retailUid, retailHandle);

            var (results, _) = await CaptureReplayDriver.ReplayAsync(rig, windows, maxGroups);
            var firstMapLoad = results.FindIndex(r => r.C2SOps.Contains(1304));
            var diffs = results.SelectMany(r => ConformanceReport.CompareBodies(r, retailHandle, rig.OurHandle, retailUid, rig.StandInUid)).ToList();
            return new Run(results, firstMapLoad < 0 ? results.Count : firstMapLoad + 1, diffs, load, seeded, t0.Elapsed - load - seeded);
        }
        return null;
    }
}

/// <summary>Always-on checks of the mass harness's own logic (no captures, no Docker).</summary>
public sealed class CaptureReplayMassLogicTests
{
    [Fact]
    public void Pick_spreads_the_sample_over_the_frame_ranking_and_honours_the_bounds()
    {
        var manifest = Enumerable.Range(1, 100).Select(i => new MassSessions.Entry("s" + i, i * 100)).ToList();
        var pick = MassSessions.Pick(manifest, 10, 1_000, 9_000);

        Assert.Equal(10, pick.Count);
        Assert.All(pick, e => Assert.InRange(e.Frames, 1_000, 9_000));
        Assert.True(pick.Zip(pick.Skip(1)).All(p => p.First.Frames < p.Second.Frames));
        Assert.True(pick[0].Frames < 2_000 && pick[^1].Frames > 8_000);                       // both ends of the range are represented
        Assert.Equal(81, MassSessions.Pick(manifest, 0, 1_000, 9_000).Count);                 // 0 = all of the pool
    }

    [Fact]
    public void Aggregate_counts_sessions_per_opcode_and_splits_consistent_from_sporadic()
    {
        WindowResult Window(ushort[] expected, ushort[] ours) => new WindowResult
        {
            Index = 0, C2SOps = [1417], Handler = "x", Expected = [.. expected], RetailAmbient = [], Ours = [.. ours],
        }.Score();

        var agg = new MassAggregate();
        for (var s = 0; s < 10; s++)
        {
            // 2310 is missed in every session, 2999 extra in every session, 2595 missed in one session only.
            var expected = s == 0 ? new ushort[] { 2310, 2558, 2595 } : [2310, 2558];
            agg.Add([Window(expected, [2558, 2999])], [], 1);
        }

        Assert.Equal(10, agg.Ops[2310].SessMissed);
        Assert.Equal(1, agg.Ops[2595].SessMissed);
        Assert.Equal(10, agg.Ops[2999].SessExtra);
        var report = agg.Format("test");
        var consistent = report.IndexOf("CONSISTENT", StringComparison.Ordinal);
        var sporadic = report.IndexOf("SPORADIC", StringComparison.Ordinal);
        Assert.True(report.IndexOf(" 2310 ", consistent, StringComparison.Ordinal) is > 0 and var at && at < sporadic);
        Assert.True(report.IndexOf(" 2595 ", sporadic, StringComparison.Ordinal) > sporadic);
    }
}
