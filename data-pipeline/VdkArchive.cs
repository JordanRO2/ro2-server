using System.IO.Compression;
using System.Text;

namespace Ro2.DataPipeline;

/// <summary>Text encodings the client formats use (EUC-KR names, CP949 tables).</summary>
internal static class Encodings
{
    /// <summary>VDK entry names (code page 51949, EUC-KR).</summary>
    public static Encoding EucKr { get; } = Get(51949);

    /// <summary>Superset of EUC-KR, used to decode non-UTF-8 .tbl text.</summary>
    public static Encoding Cp949 { get; } = Get(949);

    private static Encoding Get(int codePage)
    {
        // Initializers run before a static constructor body, so register the provider here.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }
}

internal sealed class VdkEntry
{
    public required string Name { get; init; }
    /// <summary>Archive-relative path with '/' separators.</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required uint UncompressedSize { get; init; }
    public required uint CompressedSize { get; init; }
    /// <summary>Absolute position of the payload (just after the 145-byte entry).</summary>
    public required long DataPosition { get; init; }
}

/// <summary>
/// Read-only VDK ("VDISK1.0"/"VDISK1.1") archive reader: header, entry tree and payload extraction.
/// Repacking is not supported.
/// </summary>
internal sealed class VdkArchive : IDisposable
{
    private const int EntrySize = 145;   // fixed entry record size
    private const int NameSize = 128;    // fixed-width name field

    private readonly FileStream _stream;

    public string FilePath { get; }
    public string Version { get; private set; } = "";
    public uint FileCount { get; private set; }
    public uint FolderCount { get; private set; }
    public List<VdkEntry> Entries { get; } = [];

    private VdkArchive(string path)
    {
        FilePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }

    public IEnumerable<VdkEntry> Files =>
        Entries.Where(e => !e.IsDirectory && e.Name != "." && e.Name != "..");

    public static VdkArchive Open(string path)
    {
        var archive = new VdkArchive(path);
        try
        {
            archive.Load();
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    // Header. It is 24 bytes (1.0) or 28 bytes (1.1).
    private void Load()
    {
        using var reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);

        byte[] versionBytes = reader.ReadBytes(8);
        int nul = Array.IndexOf(versionBytes, (byte)0);
        if (nul < 0) nul = 8;
        Version = Encoding.ASCII.GetString(versionBytes, 0, nul);

        uint u32At8 = reader.ReadUInt32();
        FileCount = reader.ReadUInt32();
        FolderCount = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // totalSize, unused

        if (Version == "VDISK1.0")
        {
            if (u32At8 != 4294967040) // 0xFFFFFF00
                throw new InvalidDataException("Invalid VDISK1.0 magic");
        }
        else if (Version == "VDISK1.1")
        {
            uint validation = reader.ReadUInt32();
            if (validation != FileCount * 264 + 4)
                throw new InvalidDataException("Invalid VDISK1.1 validation");
        }
        else
        {
            throw new InvalidDataException($"Unknown VDK format: {Version}");
        }

        ParseEntries(reader, "");
    }

    // Entry tree. Pre-order DFS: a file entry is followed by its payload, a directory
    // entry by its "." / ".." and children; nextOffset == 0 ends a sibling chain.
    private void ParseEntries(BinaryReader reader, string currentPath)
    {
        while (true)
        {
            if (_stream.Position + EntrySize > _stream.Length)
                break;

            byte[] entry = reader.ReadBytes(EntrySize);
            bool isDir = entry[0] != 0;

            int nameEnd = Array.IndexOf(entry, (byte)0, 1, NameSize);
            if (nameEnd < 0) nameEnd = 1 + NameSize;
            string name = Encodings.EucKr.GetString(entry, 1, nameEnd - 1);

            uint uncompressed = BitConverter.ToUInt32(entry, 129);
            uint compressed = BitConverter.ToUInt32(entry, 133);
            uint nextOffset = BitConverter.ToUInt32(entry, 141);

            string fullPath = currentPath.Length == 0 ? name : currentPath + "/" + name;
            long dataPosition = _stream.Position;

            Entries.Add(new VdkEntry
            {
                Name = name,
                Path = fullPath,
                IsDirectory = isDir,
                UncompressedSize = uncompressed,
                CompressedSize = compressed,
                DataPosition = dataPosition,
            });

            if (isDir)
            {
                if (name != "." && name != "..")
                    ParseEntries(reader, fullPath);
            }
            else
            {
                _stream.Seek(dataPosition + compressed, SeekOrigin.Begin);
            }

            if (nextOffset == 0)
                break;
        }
    }

    // Payload is a zlib stream (2-byte header + deflate + adler32), stored
    // raw when compressedSize == uncompressedSize.
    public byte[] ReadFile(VdkEntry entry)
    {
        if (entry.DataPosition + entry.CompressedSize > _stream.Length)
            throw new InvalidDataException($"{entry.Path}: payload runs past end of {FilePath}");

        byte[] raw = new byte[entry.CompressedSize];
        _stream.Seek(entry.DataPosition, SeekOrigin.Begin);
        _stream.ReadExactly(raw);

        if (entry.UncompressedSize == entry.CompressedSize)
            return raw;

        byte[] result;
        try
        {
            result = Inflate(raw, zlibHeader: true, entry.UncompressedSize);
        }
        catch (InvalidDataException)
        {
            // Fall back to a headerless deflate stream.
            result = Inflate(raw, zlibHeader: false, entry.UncompressedSize);
        }

        if (result.Length != entry.UncompressedSize)
            throw new InvalidDataException(
                $"{entry.Path}: inflated {result.Length} bytes, entry says {entry.UncompressedSize}");
        return result;
    }

    private static byte[] Inflate(byte[] data, bool zlibHeader, uint expectedSize)
    {
        using var input = new MemoryStream(data);
        using Stream inflater = zlibHeader
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream((int)Math.Min(expectedSize, int.MaxValue));
        inflater.CopyTo(output);
        return output.ToArray();
    }

    public void Dispose() => _stream.Dispose();
}
