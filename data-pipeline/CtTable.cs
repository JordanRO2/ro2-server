using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ro2.DataPipeline;

/// <summary>
/// A parsed CT ("Custom Table") file. Ported from the read path of
/// tools-ragnarok-online-2-vdk/src/VDKTool.Core/CTProcessor.cs (Read/ReadHeader/ReadString/
/// ReadValue, lines 73-105, 149-192, 219-225, 238-265). The ClosedXML/CSV/Write paths are not ported.
///
/// Layout (all little-endian):
///   64-byte header : UTF-16LE magic "RO2SEC!" or "RO2!", 2 NUL bytes, UTF-16LE timestamp
///   int32 columnCount, then columnCount strings   (int32 charCount + UTF-16LE chars)
///   int32 typeCount,   then typeCount int32 type codes
///   int32 rowCount,    then rowCount rows, each cell packed per its column type
///   uint16 CRC16 (poly 0x1021, init 0) over the packed row bytes   (CTProcessor.cs:126-145, 328-344)
///
/// Cell values are returned typed: integer types as long, FLOAT as float, BOOL as bool, STRING as string.
/// DWORD_HEX is returned as a number (the old xlsx path carried it as "0x..." text and lost it to 0).
/// </summary>
internal sealed class CtTable
{
    private const int HeaderSize = 64;    private const string MagicNew = "RO2SEC!";    private const string MagicOld = "RO2!";
    public const int Byte = 2, Short = 3, Word = 4, Int = 5, Dword = 6, DwordHex = 7,
        String = 8, Float = 9, Int64 = 11, Bool = 12;
    public string Magic { get; private set; } = "";
    public string Timestamp { get; private set; } = "";
    public string[] Headers { get; private set; } = [];
    public int[] TypeCodes { get; private set; } = [];
    public List<object[]> Rows { get; } = [];
    /// <summary>True/false when the trailing CRC16 was present and compared; null if absent.</summary>
    public bool? CrcOk { get; private set; }
    public HashSet<int> UnknownTypes { get; } = [];

    public static CtTable Parse(byte[] data)
    {
        var table = new CtTable();
        var r = new Cursor(data);

        table.ReadHeader(r);

        int columnCount = r.ReadInt32();
        r.RequireCount(columnCount, 4, "column count");
        table.Headers = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
            table.Headers[i] = r.ReadString();

        int typeCount = r.ReadInt32();
        r.RequireCount(typeCount, 4, "type count");
        table.TypeCodes = new int[typeCount];
        for (int i = 0; i < typeCount; i++)
            table.TypeCodes[i] = r.ReadInt32();

        int rowCount = r.ReadInt32();
        if (rowCount < 0 || (typeCount > 0 && (long)rowCount * typeCount > r.Remaining))
            throw new InvalidDataException($"CT row count {rowCount} is not plausible for {r.Remaining} bytes");

        int rowsStart = r.Position;
        table.Rows.Capacity = rowCount;
        for (int i = 0; i < rowCount; i++)
        {
            var row = new object[typeCount];
            for (int j = 0; j < typeCount; j++)
                row[j] = table.ReadValue(r, table.TypeCodes[j]);
            table.Rows.Add(row);
        }

        if (r.Remaining >= 2)
        {
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(r.Position, 2));
            table.CrcOk = Crc16(data.AsSpan(rowsStart, r.Position - rowsStart)) == stored;
        }
        return table;
    }

    private void ReadHeader(Cursor r)
    {
        ReadOnlySpan<byte> header = r.ReadBytes(HeaderSize);
        byte[] newMagic = Encoding.Unicode.GetBytes(MagicNew);
        byte[] oldMagic = Encoding.Unicode.GetBytes(MagicOld);

        byte[] magic;
        if (header.StartsWith(newMagic)) { magic = newMagic; Magic = MagicNew; }
        else if (header.StartsWith(oldMagic)) { magic = oldMagic; Magic = MagicOld; }
        else throw new InvalidDataException("Invalid CT file magic (expected RO2SEC! or RO2!)");

        int tsStart = magic.Length + 2;
        int tsEnd = tsStart;
        while (tsEnd < header.Length - 1)
        {
            if (header[tsEnd] == 0 && header[tsEnd + 1] == 0) break;
            tsEnd += 2;
        }
        Timestamp = tsEnd > tsStart ? Encoding.Unicode.GetString(header[tsStart..tsEnd]) : "";
    }

    private object ReadValue(Cursor r, int typeCode)
    {
        switch (typeCode)
        {
            case Byte: return (long)r.ReadByte();
            case Bool: return r.ReadByte() != 0;
            case Short: return (long)r.ReadInt16();
            case Word: return (long)r.ReadUInt16();
            case Int: return (long)r.ReadInt32();
            case Dword: return (long)r.ReadUInt32();
            case DwordHex: return (long)r.ReadUInt32();
            case Float: return r.ReadSingle();
            case Int64: return r.ReadInt64();
            case String: return r.ReadString();
            default:
                UnknownTypes.Add(typeCode);
                return (long)r.ReadInt32();
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>Bounds-checked little-endian reader over a byte array.</summary>
    private sealed class Cursor(byte[] data)
    {
        public int Position { get; private set; }
        public int Remaining => data.Length - Position;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > Remaining)
                throw new InvalidDataException($"CT truncated at offset {Position} (need {n}, have {Remaining})");
            var span = data.AsSpan(Position, n);
            Position += n;
            return span;
        }

        public ReadOnlySpan<byte> ReadBytes(int n) => Take(n);
        public byte ReadByte() => Take(1)[0];
        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public string ReadString()
        {
            int length = ReadInt32();
            if (length == 0) return "";
            if (length < 0 || (long)length * 2 > Remaining)
                throw new InvalidDataException($"CT string length {length} out of range at offset {Position}");
            return Encoding.Unicode.GetString(Take(length * 2));
        }

        public void RequireCount(int count, int minBytesEach, string what)
        {
            if (count < 0 || (long)count * minBytesEach > Remaining)
                throw new InvalidDataException($"CT {what} {count} is not plausible for {Remaining} bytes");
        }
    }
}

/// <summary>A table ready to serialise: unique column keys (in order) and rows aligned to them.</summary>
internal sealed class RecordSet(string[] keys, List<object[]> rows)
{
    public string[] Keys { get; } = keys;
    public List<object[]> Rows { get; } = rows;

    /// <summary>
    /// Applies the converter rules ported from the original Python xlsx converter:
    /// blank column names become col_N (N = 0-based column index); a duplicated column name
    /// keeps the position of its first occurrence and the value of its last (Python dict assignment);
    /// rows whose first column is 0 or empty are skipped.
    /// </summary>
    public static RecordSet FromCt(CtTable t)
    {
        int n = t.TypeCodes.Length;
        if (n == 0) return new RecordSet([], []);

        var keys = new List<string>(n);
        var keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastColumn = new List<int>(n);
        for (int j = 0; j < n; j++)
        {
            string h = j < t.Headers.Length ? t.Headers[j] : "";
            string name = h.Length == 0 ? string.Create(CultureInfo.InvariantCulture, $"col_{j}") : h;
            if (keyIndex.TryGetValue(name, out int k))
            {
                lastColumn[k] = j;
            }
            else
            {
                keyIndex[name] = keys.Count;
                keys.Add(name);
                lastColumn.Add(j);
            }
        }

        bool identity = keys.Count == n;
        var rows = new List<object[]>(t.Rows.Count);
        foreach (var row in t.Rows)
        {
            object[] rec = row;
            if (!identity)
            {
                rec = new object[keys.Count];
                for (int k = 0; k < rec.Length; k++) rec[k] = row[lastColumn[k]];
            }
            if (IsZeroOrEmpty(rec[0])) continue;
            rows.Add(rec);
        }
        return new RecordSet(keys.ToArray(), rows);
    }

    public static bool IsZeroOrEmpty(object v) => v switch
    {
        long l => l == 0,
        double d => d == 0,
        float f => f == 0,
        bool b => !b,
        string s => s.Length == 0,
        _ => false,
    };
}
