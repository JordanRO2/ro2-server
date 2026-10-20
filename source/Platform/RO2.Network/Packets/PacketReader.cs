using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace RO2.Network.Packets;

/// <summary>
/// High-performance binary packet reader using Span for zero-copy operations
/// </summary>
public ref struct PacketReader
{
    /// <summary>Bytes per UTF-16LE code unit; wide-string lengths and fixed sizes are counted in code units.</summary>
    private const int WideCharSize = sizeof(char);

    /// <summary>Size of the UTF-16LE terminator (one zero code unit) that ends a null-terminated wide string.</summary>
    private const int WideTerminatorSize = WideCharSize;

    private ReadOnlySpan<byte> _buffer;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    public bool HasData => _position < _buffer.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
            ThrowEndOfBuffer();
        return _buffer[_position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte ReadSByte() => (sbyte)ReadByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadByte() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16()
    {
        EnsureRemaining(sizeof(short));
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer[_position..]);
        _position += sizeof(short);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        EnsureRemaining(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_position..]);
        _position += sizeof(ushort);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        EnsureRemaining(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_position..]);
        _position += sizeof(int);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        EnsureRemaining(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]);
        _position += sizeof(uint);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        EnsureRemaining(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_position..]);
        _position += sizeof(long);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        EnsureRemaining(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_position..]);
        _position += sizeof(ulong);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingle()
    {
        EnsureRemaining(sizeof(float));
        var value = BinaryPrimitives.ReadSingleLittleEndian(_buffer[_position..]);
        _position += sizeof(float);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble()
    {
        EnsureRemaining(sizeof(double));
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer[_position..]);
        _position += sizeof(double);
        return value;
    }

    /// <summary>
    /// Read a length-prefixed UTF-8 string (2-byte length prefix = byte count)
    /// </summary>
    public string ReadString()
    {
        var length = ReadUInt16();
        if (length == 0)
            return string.Empty;

        EnsureRemaining(length);
        var str = Encoding.UTF8.GetString(_buffer.Slice(_position, length));
        _position += length;
        return str;
    }

    /// <summary>
    /// Read a length-prefixed Unicode string (2-byte length prefix = code-unit count, UTF-16LE)
    /// </summary>
    public string ReadUnicodeString()
    {
        var length = ReadUInt16();
        if (length == 0)
            return string.Empty;

        var byteLength = length * WideCharSize;
        EnsureRemaining(byteLength);
        var str = Encoding.Unicode.GetString(_buffer.Slice(_position, byteLength));
        _position += byteLength;
        return str;
    }

    /// <summary>
    /// Read a fixed-length string (null-terminated within buffer)
    /// </summary>
    public string ReadFixedString(int maxLength)
    {
        EnsureRemaining(maxLength);
        var slice = _buffer.Slice(_position, maxLength);
        var nullIndex = slice.IndexOf((byte)0);
        var actualLength = nullIndex >= 0 ? nullIndex : maxLength;
        var str = Encoding.UTF8.GetString(slice[..actualLength]);
        _position += maxLength;
        return str;
    }

    /// <summary>
    /// Read a null-terminated wide string (UTF-16LE).
    /// Scans for null terminator (0x0000) and reads the string.
    /// </summary>
    public string ReadWideString()
    {
        var remaining = _buffer[_position..];

        // Find null terminator (2 consecutive zero bytes on even boundary)
        int charCount = 0;
        for (int i = 0; i + 1 < remaining.Length; i += WideCharSize)
        {
            if (remaining[i] == 0 && remaining[i + 1] == 0)
                break;
            charCount++;
        }

        var byteCount = charCount * WideCharSize;
        EnsureRemaining(byteCount + WideTerminatorSize);

        var str = Encoding.Unicode.GetString(_buffer.Slice(_position, byteCount));
        _position += byteCount + WideTerminatorSize; // Skip past null terminator

        return str;
    }

    /// <summary>
    /// Read a fixed-length wide string (UTF-16LE, null-terminated within fixed size)
    /// </summary>
    public string ReadWideStringFixed(int charCount)
    {
        var byteCount = charCount * WideCharSize;
        EnsureRemaining(byteCount);

        var slice = _buffer.Slice(_position, byteCount);

        // Find null terminator
        int actualChars = 0;
        for (int i = 0; i + 1 < byteCount; i += WideCharSize)
        {
            if (slice[i] == 0 && slice[i + 1] == 0)
                break;
            actualChars++;
        }

        var str = Encoding.Unicode.GetString(slice[..(actualChars * WideCharSize)]);
        _position += byteCount;

        return str;
    }

    /// <summary>
    /// Read raw bytes
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureRemaining(count);
        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>
    /// Read raw bytes into provided buffer
    /// </summary>
    public void ReadBytes(Span<byte> destination)
    {
        EnsureRemaining(destination.Length);
        _buffer.Slice(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }

    /// <summary>
    /// Skip bytes without reading
    /// </summary>
    public void Skip(int count)
    {
        EnsureRemaining(count);
        _position += count;
    }

    /// <summary>
    /// Seek to absolute position
    /// </summary>
    public void Seek(int position)
    {
        if (position < 0 || position > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(position));
        _position = position;
    }

    /// <summary>
    /// Get remaining buffer as span
    /// </summary>
    public ReadOnlySpan<byte> GetRemainingSpan() => _buffer[_position..];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRemaining(int count)
    {
        // Subtraction, not _position + count: a hostile count near int.MaxValue wraps the sum negative
        // and would pass; a negative count (Skip(-n)) would move the cursor backwards.
        if (count < 0 || count > _buffer.Length - _position)
            ThrowEndOfBuffer();
    }

    /// <summary>Message of the exception a short or hostile read throws; log sinks recognise it as a malformed packet.</summary>
    internal const string EndOfBufferMessage = "Attempted to read past end of packet buffer";

    private static void ThrowEndOfBuffer()
    {
        throw new InvalidOperationException(EndOfBufferMessage);
    }
}
