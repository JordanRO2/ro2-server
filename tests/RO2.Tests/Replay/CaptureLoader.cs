using System.Globalization;
using System.Text.Json;

namespace RO2.Tests.Replay;

/// <summary>One decrypted RMI frame from a retail capture: direction, opcode, handler body and the capture clock.</summary>
/// <param name="Row">Zero-based line number in the .jsonl, for tracing a frame back to the file.</param>
/// <param name="Ts">Seconds since midnight from the row's <c>HH:MM:SS.mmm</c> stamp.</param>
/// <param name="Server">ZONE or CHAT.</param>
internal sealed record CapFrame(int Row, double Ts, bool IsC2S, string Server, ushort Op, byte[] Body)
{
    public override string ToString() => $"{(IsC2S ? "C2S" : "S2C")} {Op} ({Body.Length} B) row {Row}";
}

/// <summary>An ordered run of frames that belongs to one client connection to the Zone.</summary>
internal sealed record CapConnection(int Index, IReadOnlyList<CapFrame> Frames);

/// <summary>What the loader dropped, so a report can show the capture was read completely.</summary>
internal sealed record CaptureStats(int PktRows, int Kept, int Keepalives, int Blobs, int OtherServers);

/// <summary>
/// Reads an <c>RO2Mods_Capture_*.jsonl</c> retail capture. The <c>data</c> of each <c>pkt</c> row is already decrypted plaintext;
/// this strips the transport header (<c>01 op op</c> for UNENC, <c>counter(2) 01 op op</c> for AES/RC4), drops keepalives and
/// the 64-byte handshake key blobs, and cuts the ZONE stream into connections at each 1303.
/// </summary>
internal static class CaptureLoader
{
    public const ushort KeepaliveA = 1001;
    public const ushort KeepaliveB = 1019;
    public const ushort ZoneAuthOp = 1303;

    public static (IReadOnlyList<CapConnection> Connections, CaptureStats Stats) Load(string path)
        => Parse(File.ReadLines(path));

    public static (IReadOnlyList<CapConnection> Connections, CaptureStats Stats) Parse(IEnumerable<string> lines)
    {
        var frames = new List<CapFrame>();
        int row = -1, pkts = 0, keepalives = 0, blobs = 0, other = 0;
        foreach (var line in lines)
        {
            row++;
            if (line.Length == 0 || !line.Contains("\"type\":\"pkt\"", StringComparison.Ordinal)) continue;
            pkts++;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var srv = r.GetProperty("srv").GetString() ?? "";
            if (srv is "LOGIN") { other++; continue; }
            var enc = r.GetProperty("enc").GetString();
            var data = Convert.FromHexString(r.GetProperty("data").GetString() ?? "");

            // UNENC is [01][op lo][op hi], AES/RC4 is [counter lo][counter hi][01][op lo][op hi].
            var head = enc == "UNENC" ? 0 : 2;
            var headLen = head + 3;
            if (data.Length < headLen || data[head] != 0x01) { blobs++; continue; }
            var op = (ushort)(data[head + 1] | data[head + 2] << 8);
            if (op is KeepaliveA or KeepaliveB) { keepalives++; continue; }
            // Handshake key blobs parse to a random "opcode"; real Zone/Chat opcodes sit in these two ranges.
            if (!(op is >= 1000 and < 4000 || op is >= 23000 and < 24000)) { blobs++; continue; }

            // Rows after the zone entry carry no server tag; the opcode range says which server they belong to.
            var server = op >= 23000 ? "CHAT" : "ZONE";
            frames.Add(new CapFrame(row, ParseClock(r.GetProperty("ts").GetString()!),
                r.GetProperty("dir").GetString() == "C2S", server, op, data[headLen..]));
        }

        var zone = frames.Where(f => f.Server == "ZONE").ToList();
        return (Split(zone), new CaptureStats(pkts, frames.Count, keepalives, blobs, other));
    }

    /// <summary>Cuts the ZONE stream at each 1303; the S2C 2303 greeting that precedes a 1303 goes with it.</summary>
    private static List<CapConnection> Split(List<CapFrame> zone)
    {
        var cuts = new List<int>();
        for (var i = 0; i < zone.Count; i++)
        {
            if (!zone[i].IsC2S || zone[i].Op != ZoneAuthOp) continue;
            var start = i > 0 && !zone[i - 1].IsC2S && zone[i - 1].Op == 2303 ? i - 1 : i;
            cuts.Add(start);
        }
        var result = new List<CapConnection>();
        if (cuts.Count == 0) return result;
        cuts[0] = 0; // anything before the first 1303 is still this connection's
        for (var c = 0; c < cuts.Count; c++)
        {
            var end = c + 1 < cuts.Count ? cuts[c + 1] : zone.Count;
            result.Add(new CapConnection(c, zone.GetRange(cuts[c], end - cuts[c])));
        }
        return result;
    }

    private static double ParseClock(string ts)
        => TimeSpan.ParseExact(ts, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture).TotalSeconds;

    /// <summary>Capture files to replay: <c>RO2_CAPTURE_DIR</c>, else the known SHIPPING folder. Empty when the folder is absent.</summary>
    public static IReadOnlyList<string> FindCaptureFiles()
    {
        var dir = Environment.GetEnvironmentVariable("RO2_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = DefaultDir;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "RO2Mods_Capture_*.jsonl").Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public const string DefaultDir =
        @"C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\clients\ClientArchive\b303-2026-07-11-Optimizations\SHIPPING";
}
