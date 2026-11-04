namespace RO2.Tests.Replay;

/// <summary>Walks the retail windows of one connection through a <see cref="CaptureReplayRig"/> and scores each group of C2S frames.</summary>
internal static class CaptureReplayDriver
{
    /// <summary>Opcodes that end the replay: the client leaves (logout, channel move) and the stand-in session stops meaning anything.</summary>
    public static readonly HashSet<ushort> StopAt = [1302, 1420, 1421];

    /// <param name="maxGroups">Stop after this many window groups (a bound for mass runs; every group costs at least one idle wait).</param>
    public static async Task<(List<WindowResult> Results, int Replayed)> ReplayAsync(CaptureReplayRig rig, IReadOnlyList<RetailWindow> windows,
        int maxGroups = int.MaxValue)
    {
        var results = new List<WindowResult>();
        var replayed = 0;
        foreach (var group in ConformanceReport.Group(windows))
        {
            if (group.Any(w => StopAt.Contains(w.C2S.Op)) || results.Count >= maxGroups) break;
            var ours = new List<ushort>();
            var ourFrames = new List<(ushort Op, byte[] Body)>();
            var warnings = new List<string>();
            var missing = new List<ushort>();
            var closed = false;
            foreach (var w in group)
            {
                if (!rig.HasHandler(w.C2S.Op)) missing.Add(w.C2S.Op);
                var (frames, warn, wasClosed) = await rig.SendAsync(w.C2S.Op, w.C2S.Body);
                ours.AddRange(frames.Select(f => f.Op));
                ourFrames.AddRange(frames);
                warnings.AddRange(warn);
                replayed++;
                if (wasClosed) { closed = true; break; }
            }
            var ops = group.Select(w => w.C2S.Op).ToList();
            results.Add(new WindowResult
            {
                Index = group[0].Index,
                C2SOps = ops,
                Handler = string.Join('+', ops.Distinct().Select(o => rig.HandlerName(o) ?? "(none)")),
                Expected = group.SelectMany(w => w.Reactive).Select(f => f.Op).ToList(),
                RetailAmbient = group.SelectMany(w => w.Ambient).Select(f => f.Op).ToList(),
                Ours = ours,
                OurFrames = ourFrames,
                ExpectedFrames = group.SelectMany(w => w.Reactive).ToList(),
                Warnings = warnings,
                Note = string.Join("; ", new[]
                {
                    missing.Count > 0 ? "no handler registered for C2S " + string.Join(',', missing.Distinct()) : null,
                    closed ? "SERVER CLOSED THE CONNECTION here; replay stops" : null,
                }.Where(n => n is not null)) is { Length: > 0 } note ? note : null,
            }.Score());
            if (closed) break;
        }
        return (results, replayed);
    }
}
