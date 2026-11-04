using System.Text;

namespace RO2.Tests.Replay;

/// <summary>One replayed C2S: what retail answered next to it and what our server answered.</summary>
internal sealed class WindowResult
{
    public required int Index { get; init; }
    /// <summary>The C2S frames this window covers: one, or a burst the retail server answered together (see <see cref="ConformanceReport.Group"/>).</summary>
    public required List<ushort> C2SOps { get; init; }
    public required string Handler { get; init; }
    /// <summary>Retail S2C opcodes between this C2S and the next one, ambient ones removed (in order).</summary>
    public required List<ushort> Expected { get; init; }
    /// <summary>Retail S2C opcodes dropped as ambient from this window.</summary>
    public required List<ushort> RetailAmbient { get; init; }
    /// <summary>Our S2C opcodes, in the order the client read them.</summary>
    public required List<ushort> Ours { get; init; }
    public List<string> Warnings { get; init; } = [];
    /// <summary>The frames behind <see cref="Expected"/> and <see cref="Ours"/> (same order), for the Tier C byte compare.</summary>
    public List<CapFrame> ExpectedFrames { get; init; } = [];
    public List<(ushort Op, byte[] Body)> OurFrames { get; init; } = [];
    public string? Note { get; init; }

    // Filled by Score().
    public List<ushort> OursKept { get; private set; } = [];
    public int OursAmbientDropped { get; private set; }
    public List<ushort> Matched { get; private set; } = [];
    public List<ushort> Missed { get; private set; } = [];
    public List<ushort> Extra { get; private set; } = [];
    public int Lcs { get; private set; }
    public int OrderDenominator => Math.Max(Expected.Count, OursKept.Count);

    /// <summary>
    /// Tier A (opcode multiset) and Tier B (order, by longest common subsequence). An opcode of ours that retail also sent in this
    /// window but classed ambient is not held against us: it is dropped from our side first.
    /// </summary>
    public WindowResult Score()
    {
        var left = Expected.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());
        var ambient = RetailAmbient.ToHashSet();
        OursKept = []; Matched = []; Extra = []; OursAmbientDropped = 0;
        foreach (var op in Ours)
        {
            if (left.TryGetValue(op, out var n) && n > 0) { left[op] = n - 1; Matched.Add(op); OursKept.Add(op); }
            else if (ambient.Contains(op)) OursAmbientDropped++;
            else { Extra.Add(op); OursKept.Add(op); }
        }
        Missed = left.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).Order().ToList();
        Lcs = ConformanceReport.LongestCommonSubsequence(Expected, OursKept);
        return this;
    }
}

internal static class ConformanceReport
{
    /// <summary>Opcodes known to be server-driven ticks and other actors' traffic, whatever the empirical pass finds.</summary>
    private static readonly ushort[] KnownAmbient = [2750, 2318, 2401, 2498, 2811, 2494];

    /// <summary>An S2C frame this long after the last C2S counts as unprompted.</summary>
    private const double UnpromptedGapSeconds = 1.0;

    /// <summary>An ambient opcode sent in the same server flush as a non-ambient frame (the 2498 of the 1417 burst) stays reactive.</summary>
    private const double SameFlushSeconds = 0.015;

    /// <summary>The ambient opcode set of one connection: the known list plus every opcode that is mostly sent with no C2S just before it.</summary>
    public static Dictionary<ushort, string> AmbientSet(IReadOnlyList<CapFrame> frames)
    {
        var set = KnownAmbient.ToDictionary(o => o, _ => "known list");
        var total = new Dictionary<ushort, int>();
        var unprompted = new Dictionary<ushort, int>();
        var lastC2S = double.NegativeInfinity;
        foreach (var f in frames)
        {
            if (f.IsC2S) { lastC2S = f.Ts; continue; }
            total[f.Op] = total.GetValueOrDefault(f.Op) + 1;
            if (f.Ts - lastC2S >= UnpromptedGapSeconds) unprompted[f.Op] = unprompted.GetValueOrDefault(f.Op) + 1;
        }
        foreach (var (op, n) in total)
        {
            var u = unprompted.GetValueOrDefault(op);
            if (n >= 5 && u * 2 >= n && !set.ContainsKey(op)) set[op] = $"empirical {u}/{n} unprompted";
        }
        return set;
    }

    /// <summary>
    /// One <see cref="RetailWindow"/> per C2S of the connection: the S2C frames up to the next C2S, split into reactive and ambient.
    /// </summary>
    public static List<RetailWindow> BuildWindows(IReadOnlyList<CapFrame> frames, IReadOnlyDictionary<ushort, string> ambient)
    {
        var windows = new List<RetailWindow>();
        List<CapFrame>? current = null;
        CapFrame? c2s = null;
        void Close()
        {
            if (c2s is null || current is null) return;
            var reactive = new List<CapFrame>();
            var dropped = new List<CapFrame>();
            foreach (var f in current)
            {
                var isAmbient = ambient.ContainsKey(f.Op)
                    && !current.Any(o => !ambient.ContainsKey(o.Op) && Math.Abs(o.Ts - f.Ts) <= SameFlushSeconds);
                (isAmbient ? dropped : reactive).Add(f);
            }
            windows.Add(new RetailWindow(windows.Count, c2s, current, reactive, dropped));
        }
        foreach (var f in frames)
        {
            if (f.IsC2S) { Close(); c2s = f; current = []; }
            else current?.Add(f);
        }
        Close();
        return windows;
    }


    /// <summary>
    /// Folds a retail window with no reactive S2C into the next one when the next C2S follows within <see cref="BurstSeconds"/> or repeats
    /// its opcode. Retail answers a burst of C2S together after the last one (the client sends 1592 1596 1593 1593 1599 in one
    /// millisecond and the replies follow the 1599), and answers a resent C2S (a second 1417) once, so a per-C2S window would
    /// charge the replies to the wrong request.
    /// </summary>
    public static List<List<RetailWindow>> Group(IReadOnlyList<RetailWindow> windows)
    {
        var groups = new List<List<RetailWindow>>();
        List<RetailWindow>? current = null;
        foreach (var w in windows)
        {
            current ??= [];
            current.Add(w);
            var next = w.Index + 1 < windows.Count ? windows[w.Index + 1] : null;
            var folds = w.Reactive.Count == 0 && next is not null
                && (next.C2S.Op == w.C2S.Op || next.C2S.Ts - w.C2S.Ts < BurstSeconds);
            if (folds) continue;
            groups.Add(current);
            current = null;
        }
        if (current is not null) groups.Add(current);
        return groups;
    }

    private const double BurstSeconds = 0.25;


    /// <summary>Opcodes whose body is a wall-clock value that differs on every run, so it is masked whole.</summary>
    private static readonly HashSet<ushort> ClockBodies = [2669, 2811];

    public sealed record FrameDiff(int Window, ushort Op, int RetailLen, int OurLen, int DiffBytes, int Compared, string FirstDiffs)
    {
        public bool Identical => DiffBytes == 0;
    }

    /// <summary>
    /// Tier C: pairs the n-th retail and n-th own frame of each opcode in a window and compares their bodies byte for byte. Masked first:
    /// the stand-in's identity (retail's actor handle and character uid against ours, which cannot match) and the clock bodies
    /// (<see cref="ClockBodies"/>). Everything else must be equal, so a diff is a real difference in content or layout.
    /// </summary>
    public static List<FrameDiff> CompareBodies(WindowResult w, uint retailHandle, uint ourHandle, ulong retailUid, ulong ourUid)
    {
        var diffs = new List<FrameDiff>();
        foreach (var op in w.ExpectedFrames.Select(f => f.Op).Distinct())
        {
            var re = w.ExpectedFrames.Where(f => f.Op == op).ToList();
            var ou = w.OurFrames.Where(f => f.Op == op).ToList();
            if (ou.Count == 0) continue;
            // Retail floods some opcodes during login (2498/2499 recompute as each stat lands) then settles on a final
            // frame; we send the settled frame once. Pair the LAST k frames of each so our settled frame compares against
            // retail's settled frame, not its first partial one. For 1:1 opcodes this is the same as head alignment.
            var k = Math.Min(re.Count, ou.Count);
            for (var j = 0; j < k; j++)
            {
                var exp = re[re.Count - k + j];
                var our = ou[ou.Count - k + j];
                var a = Mask(op, exp.Body, retailHandle, retailUid);
                var b = Mask(op, our.Body, ourHandle, ourUid);
                var common = Math.Min(a.Length, b.Length);
                var bad = new List<int>();
                for (var i = 0; i < common; i++) if (a[i] != b[i]) bad.Add(i);
                diffs.Add(new FrameDiff(w.Index, op, a.Length, b.Length, bad.Count + Math.Abs(a.Length - b.Length), Math.Max(a.Length, b.Length),
                    string.Join(",", bad.Take(6)) + (bad.Count > 6 ? ",.." : "")));
            }
        }
        return diffs;
    }

    private static byte[] Mask(ushort op, byte[] body, uint handle, ulong uid)
    {
        var m = (byte[])body.Clone();
        if (ClockBodies.Contains(op)) { Array.Clear(m); return m; }
        if (op == 2307) MaskListUids(m);
        for (var i = 0; i + 4 <= m.Length; i++)
        {
            if (i + 8 <= m.Length && BitConverter.ToUInt64(m, i) == uid) { Array.Fill(m, (byte)0xBB, i, 8); i += 7; }
            else if (BitConverter.ToUInt32(m, i) == handle) { Array.Fill(m, (byte)0xAA, i, 4); i += 3; }
        }
        return m;
    }

    /// <summary>The 2307 lists every character of the account, each under its own uid; ours are built from the character id, so none of the 8-byte uids can match.</summary>
    private static void MaskListUids(byte[] m)
    {
        if (m.Length < 10) return;
        var count = BitConverter.ToUInt16(m, 8);
        var o = 10;
        for (var c = 0; c < count && o + 104 <= m.Length; c++)
        {
            Array.Fill(m, (byte)0xBB, o, 8);
            o += 102;
            o += 2 + 126 * BitConverter.ToUInt16(m, o);
        }
    }

    public static string FormatBodies(IReadOnlyList<FrameDiff> diffs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tier C, masked byte compare of matched frames in the world-entry windows (identity and clock bodies masked):");
        sb.AppendLine("  win  op    retail B   ours B   diff B  differing offsets");
        foreach (var d in diffs)
            sb.AppendLine($"  {d.Window,3}  {d.Op,-5} {d.RetailLen,8} {d.OurLen,8} {d.DiffBytes,8}  {(d.Identical ? "identical" : d.FirstDiffs)}");
        var bytes = diffs.Sum(d => d.Compared);
        sb.AppendLine($"  {diffs.Count(d => d.Identical)}/{diffs.Count} frames byte-identical, {(bytes == 0 ? 0 : 1.0 - (double)diffs.Sum(d => d.DiffBytes) / bytes):P1} of {bytes} bytes equal");
        return sb.ToString();
    }

    public static int LongestCommonSubsequence(IReadOnlyList<ushort> a, IReadOnlyList<ushort> b)
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
            for (var j = 1; j <= b.Count; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Count, b.Count];
    }

    public sealed record Totals(int Windows, int Expected, int Ours, int Matched, int Lcs, int Denominator)
    {
        public double Precision => Ours == 0 ? 1 : (double)Matched / Ours;
        public double Recall => Expected == 0 ? 1 : (double)Matched / Expected;
        public double Order => Denominator == 0 ? 1 : (double)Lcs / Denominator;
        public string Describe() => $"{Windows} windows, A precision {Precision:P0} recall {Recall:P0}, B order {Order:P0}";
    }

    public static Totals Total(IEnumerable<WindowResult> results)
    {
        var r = results.ToList();
        return new Totals(r.Count, r.Sum(w => w.Expected.Count), r.Sum(w => w.OursKept.Count),
            r.Sum(w => w.Matched.Count), r.Sum(w => w.Lcs), r.Sum(w => w.OrderDenominator));
    }

    public static string Format(string title, IReadOnlyDictionary<ushort, string> ambient, IReadOnlyList<WindowResult> results, int worldEntryWindows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Capture replay conformance: {title} ===");
        sb.AppendLine("Ambient opcodes (dropped from the retail side): "
            + string.Join(", ", ambient.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} [{kv.Value}]")));
        sb.AppendLine();
        sb.AppendLine("  #  C2S               handler                 retail reactive S2C          ours                         A tp/miss/extra  B lcs/den");
        foreach (var w in results)
        {
            var handler = w.Handler.Length > 22 ? w.Handler[..22] : w.Handler;
            sb.AppendLine($"{w.Index,3}  {Rle(w.C2SOps, 17, "+"),-17} {handler,-23} {Rle(w.Expected),-28} {Rle(w.OursKept),-28} "
                + $"{w.Matched.Count,3}/{w.Missed.Count,3}/{w.Extra.Count,3}       {w.Lcs,3}/{w.OrderDenominator,-3}");
            if (w.Missed.Count > 0) sb.AppendLine($"       missing: {Rle(w.Missed, int.MaxValue)}");
            if (w.Extra.Count > 0) sb.AppendLine($"       extra:   {Rle(w.Extra, int.MaxValue)}");
            if (w.Note is not null) sb.AppendLine($"       note:    {w.Note}");
            foreach (var m in w.Warnings.Take(3)) sb.AppendLine($"       server:  {Trim(m, 150)}");
        }
        sb.AppendLine();
        AppendTotals(sb, "World entry (first 1303 through the first 1304 window)", Total(results.Take(worldEntryWindows)));
        AppendTotals(sb, "Whole replayed session", Total(results));
        var perOp = results.SelectMany(w => w.Matched.Select(o => (o, hit: true))
                .Concat(w.Missed.Select(o => (o, hit: false))))
            .GroupBy(x => x.o).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count(x => x.hit)}/{g.Count()}");
        sb.AppendLine("Per reactive opcode, ours-matched/retail-expected: " + string.Join(" ", perOp));
        return sb.ToString();
    }

    private static void AppendTotals(StringBuilder sb, string label, Totals t)
        => sb.AppendLine($"{label}: {t.Windows} windows | Tier A precision {t.Precision:P1} ({t.Matched}/{t.Ours}), recall {t.Recall:P1} ({t.Matched}/{t.Expected}) "
            + $"| Tier B order {t.Order:P1} (LCS {t.Lcs}/{t.Denominator})");

    /// <summary>"2866 2868 2865x2" for a run of opcodes.</summary>
    public static string Rle(IReadOnlyList<ushort> ops, int max = 28, string sep = " ")
    {
        if (ops.Count == 0) return "-";
        var parts = new List<string>();
        for (var i = 0; i < ops.Count;)
        {
            var j = i;
            while (j < ops.Count && ops[j] == ops[i]) j++;
            parts.Add(j - i > 1 ? $"{ops[i]}x{j - i}" : ops[i].ToString());
            i = j;
        }
        var s = string.Join(sep, parts);
        return s.Length > max ? s[..(max - 2)] + ".." : s;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

/// <summary>The retail side of one C2S window.</summary>
internal sealed record RetailWindow(int Index, CapFrame C2S, List<CapFrame> All, List<CapFrame> Reactive, List<CapFrame> Ambient);
