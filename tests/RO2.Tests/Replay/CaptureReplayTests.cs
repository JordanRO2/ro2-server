using RO2.Tests.Integration;
using Testcontainers.MongoDb;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Replay;

/// <summary>
/// A [Fact] that is SKIPPED (never failed) when there are no retail captures to replay (set <c>RO2_CAPTURE_DIR</c>, default is the
/// SHIPPING folder of the b303-2026-07-11 client) or no Docker daemon for the throwaway mongo:7.
/// </summary>
public sealed class CaptureReplayFactAttribute : FactAttribute
{
    public CaptureReplayFactAttribute()
    {
        if (CaptureLoader.FindCaptureFiles().Count == 0)
            Skip = $"no RO2Mods_Capture_*.jsonl under {Environment.GetEnvironmentVariable("RO2_CAPTURE_DIR") ?? CaptureLoader.DefaultDir} (set RO2_CAPTURE_DIR)";
        else if (new DockerFactAttribute().Skip is { } noDocker)
            Skip = noDocker;
    }
}

/// <summary>One mongo:7 container shared by the replay, started on first use so a skipped run never touches Docker.</summary>
public sealed class ReplayMongoFixture : IAsyncLifetime
{
    private MongoDbContainer? _container;
    public string? StartupError { get; private set; }

    public async Task<string?> ConnectionStringAsync()
    {
        if (_container is not null) return _container.GetConnectionString();
        try
        {
            _container = new MongoDbBuilder("mongo:7").Build();
            await _container.StartAsync();
            return _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            StartupError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() { if (_container is not null) await _container.DisposeAsync(); }
}

/// <summary>
/// Capture-replay conformance: retail C2S frames from a real session go through the real Zone dispatch path and our S2C is scored
/// against what the retail server answered. A measurement, not a regression gate: the report is the output (use
/// <c>--logger "console;verbosity=detailed"</c>), and the asserts only prove the harness ran.
/// </summary>
public sealed class CaptureReplayTests(ITestOutputHelper output, ReplayMongoFixture mongo) : IClassFixture<ReplayMongoFixture>
{
    /// <summary>Opcodes that end the replay: the client leaves (logout, channel move) and the stand-in session stops meaning anything.</summary>
    private static readonly HashSet<ushort> StopAt = [1302, 1420, 1421];

    [CaptureReplayFact]
    public async Task Retail_sessions_replay_through_the_zone_dispatcher()
    {
        var connectionString = await mongo.ConnectionStringAsync();
        if (connectionString is null)
        {
            // No usable mongo (image pull failed, daemon died): report it loudly and stop, never fail the suite for it.
            output.WriteLine("SKIPPED: mongo:7 container did not start: " + mongo.StartupError);
            return;
        }

        var summary = new List<string>();
        foreach (var file in CaptureLoader.FindCaptureFiles())
        {
            var (connections, stats) = CaptureLoader.Load(file);
            output.WriteLine($"{Path.GetFileName(file)}: {stats.PktRows} pkt rows -> {stats.Kept} kept ({stats.Keepalives} keepalives, {stats.Blobs} handshake blobs dropped), {connections.Count} zone connection(s)");
            if (connections.Count == 0) continue;

            var connection = connections[0];
            var results = await ReplayAsync(connection, connectionString, Path.GetFileName(file));
            Assert.NotEmpty(results.Results);
            summary.Add($"{Path.GetFileName(file)}: world entry {ConformanceReport.Total(results.Results.Take(results.WorldEntryWindows)).Describe()}");
        }
        output.WriteLine("");
        output.WriteLine("SUMMARY");
        foreach (var s in summary) output.WriteLine(s);
    }

    private sealed record Replayed(List<WindowResult> Results, int WorldEntryWindows);

    private async Task<Replayed> ReplayAsync(CapConnection connection, string mongoConnectionString, string title)
    {
        var auth = connection.Frames.First(f => f.IsC2S && f.Op == CaptureLoader.ZoneAuthOp);
        var token = new Guid(auth.Body.AsSpan(10, 16));
        var ambient = ConformanceReport.AmbientSet(connection.Frames);
        var windows = ConformanceReport.BuildWindows(connection.Frames, ambient);

        await using var rig = await CaptureReplayRig.StartAsync(mongoConnectionString, token);
        // Retail's own 2310 names its character uid (body+4) and actor handle (body+12): the identity to swap in C2S bodies.
        var own = connection.Frames.FirstOrDefault(f => !f.IsC2S && f.Op == 2310);
        if (own is { Body.Length: >= 16 })
            rig.SetRetailIdentity(BitConverter.ToUInt64(own.Body, 4), BitConverter.ToUInt32(own.Body, 12));

        var results = new List<WindowResult>();
        var replayed = 0;
        foreach (var group in ConformanceReport.Group(windows))
        {
            if (group.Any(w => StopAt.Contains(w.C2S.Op))) break;
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

        var firstMapLoad = results.FindIndex(r => r.C2SOps.Contains(1304));
        var worldEntry = firstMapLoad < 0 ? results.Count : firstMapLoad + 1;
        output.WriteLine(ConformanceReport.Format(title, ambient, results, worldEntry));
        if (own is { Body.Length: >= 16 })
        {
            var retailUid = BitConverter.ToUInt64(own.Body, 4);
            var diffs = results.Take(worldEntry).SelectMany(r => ConformanceReport.CompareBodies(r,
                BitConverter.ToUInt32(own.Body, 12), rig.OurHandle, retailUid, rig.StandInUid)).ToList();
            output.WriteLine(ConformanceReport.FormatBodies(diffs));
        }
        var c2sOps = connection.Frames.Where(f => f.IsC2S).Select(f => f.Op).Distinct().Order().ToList();
        var unhandled = c2sOps.Where(o => !rig.HasHandler(o)).ToList();
        output.WriteLine($"Handler coverage: {c2sOps.Count} distinct C2S opcodes in this capture, {c2sOps.Count - unhandled.Count} have a registered Zone handler; no handler: {(unhandled.Count == 0 ? "-" : string.Join(' ', unhandled))}");
        output.WriteLine($"({windows.Count - replayed} of {windows.Count} C2S frame(s) not replayed: the replay stops at the first of {string.Join('/', StopAt)} or when the server drops the connection)");
        return new Replayed(results, worldEntry);
    }
}

/// <summary>Always-on checks of the harness's own logic (no captures, no Docker).</summary>
public sealed class CaptureReplayLogicTests
{
    private static string Row(string dir, string enc, string hex, string srv = "ZONE", string ts = "01:00:00.000")
        => $"{{\"type\":\"pkt\",\"ts\":\"{ts}\",\"dir\":\"{dir}\",\"srv\":\"{srv}\",\"enc\":\"{enc}\",\"data\":\"{hex}\"}}";

    [Fact]
    public void Loader_strips_headers_drops_keepalives_and_blobs_and_splits_at_1303()
    {
        string[] lines =
        [
            "{\"type\":\"meta\"}",
            Row("S2C", "AES", "000001ff0801090900f1d20000000020", ts: "01:00:00.100"),      // 2303 greeting, AES header
            Row("C2S", "AES", "0000011705013c3c00f2d2", ts: "01:00:00.200"),               // 1303
            Row("S2C", "UNENC", "01fe091700", srv: "", ts: "01:00:00.300"),                // 2558, untagged server
            Row("C2S", "UNENC", "01e9030000000000000000", ts: "01:00:00.400"),             // 1001 keepalive
            Row("C2S", "AES", new string('a', 128), ts: "01:00:00.500"),                   // 64-byte handshake blob
            Row("C2S", "RC4", "010001ec05", srv: "ZONE", ts: "01:00:00.600"),              // 1516? no: counter 1, then 01 ec 05
            Row("C2S", "RC4", "000001d959", srv: "CHAT", ts: "01:00:00.700"),              // chat, own server
            Row("S2C", "AES", "000001ff0801090900f1d20000000020", ts: "01:00:01.100"),     // 2303 of the next connection
            Row("C2S", "AES", "0000011705013c3c00f2d2", ts: "01:00:01.200"),               // second 1303
        ];
        var (conns, stats) = CaptureLoader.Parse(lines);

        Assert.Equal(2, conns.Count);
        Assert.Equal(1, stats.Keepalives);
        Assert.Equal(1, stats.Blobs);
        Assert.Equal([(ushort)2303, (ushort)1303, (ushort)2558, (ushort)1516], conns[0].Frames.Select(f => f.Op).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x3c, 0x3c, 0x00, 0xf2, 0xd2 }, conns[0].Frames[1].Body);   // header stripped, body kept
        Assert.Equal([(ushort)2303, (ushort)1303], conns[1].Frames.Select(f => f.Op).ToArray());    // greeting moved to its own connection
    }

    [Fact]
    public void Scoring_counts_the_multiset_and_the_order_and_ignores_ambient_extras()
    {
        var w = new WindowResult
        {
            Index = 0, C2SOps = [1417], Handler = "x",
            Expected = [2558, 2743, 2595, 2310],
            RetailAmbient = [2750],
            Ours = [2558, 2750, 2595, 2743, 2999],
        }.Score();

        Assert.Equal([(ushort)2558, (ushort)2595, (ushort)2743], w.Matched.Order().ToArray());
        Assert.Equal([(ushort)2310], w.Missed);
        Assert.Equal([(ushort)2999], w.Extra);
        Assert.Equal(1, w.OursAmbientDropped);
        Assert.Equal(2, w.Lcs);                 // 2558 2595 or 2558 2743
        Assert.Equal(4, w.OrderDenominator);
    }
}
