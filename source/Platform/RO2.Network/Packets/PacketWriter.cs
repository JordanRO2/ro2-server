using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace RO2.Network.Packets;

/// <summary>
/// Forward-only binary writer over pooled buffers.
/// </summary>
public sealed class PacketWriter : IDisposable
{
    private byte[] _buffer;
    private int _position;
    private bool _disposed;

    private const int DefaultCapacity = 1024;
    private const int MaxCapacity = 1024 * 1024; 

    /// <summary>Smallest pooled buffer the writer rents, whatever capacity is requested.</summary>
    private const int MinCapacity = 64;

    /// <summary>Initial capacity of the scratch writer that holds a compact-length body's payload.</summary>
    private const int BodyScratchCapacity = 64;

    /// <summary>Size in bytes of the 16-bit self length that follows the compact length in a byte-array body.</summary>
    private const int SelfLengthBytes = sizeof(ushort);

    // Compact-length widths (the marker byte) and the exclusive upper bound each one holds:
    // the top bit of every width is reserved, so a width N covers values below 2^(8N-1).
    private const byte CompactWidth1 = 1;
    private const byte CompactWidth2 = 2;
    private const byte CompactWidth4 = 4;
    private const byte CompactWidth8 = 8;
    private const long CompactLimit1 = 0x80;
    private const long CompactLimit2 = 0x8000;
    private const long CompactLimit4 = 0x80000000L;

    public PacketWriter(int initialCapacity = DefaultCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinCapacity));
        _position = 0;
    }

    public int Length => _position;
    public int Capacity => _buffer.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSingle(float value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDouble(double value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

 /// <summary>Writes a length-prefixed UTF-8 string (2-byte length prefix).</summary>
    public void WriteString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUInt16(0);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ArgumentException($"String too long: {byteCount} bytes (max {ushort.MaxValue})");

        WriteUInt16((ushort)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

 /// <summary>Writes a length-prefixed UTF-16LE string (2-byte length prefix).</summary>
    public void WriteUnicodeString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUInt16(0);
            return;
        }

        var charCount = value.Length;
        if (charCount > ushort.MaxValue)
            throw new ArgumentException($"String too long: {charCount} chars (max {ushort.MaxValue})");

        WriteUInt16((ushort)charCount);
        var byteCount = charCount * 2;
        EnsureCapacity(byteCount);
        Encoding.Unicode.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

 /// <summary>Writes a fixed-length string, padded with nulls.</summary>
    public void WriteFixedString(string? value, int length)
    {
        EnsureCapacity(length);
        var span = _buffer.AsSpan(_position, length);
        span.Clear();

        if (!string.IsNullOrEmpty(value))
        {
            var byteCount = Math.Min(Encoding.UTF8.GetByteCount(value), length - 1);
            Encoding.UTF8.GetBytes(value.AsSpan(), span);
        }

        _position += length;
    }

 /// <summary>Writes raw bytes.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
    }

 /// <summary>Writes zero padding.</summary>
    public void WriteZeros(int count)
    {
        EnsureCapacity(count);
        _buffer.AsSpan(_position, count).Clear();
        _position += count;
    }

 /// <summary>Reserves space and returns its position, for a later write.</summary>
    public int Reserve(int count)
    {
        EnsureCapacity(count);
        var pos = _position;
        _position += count;
        return pos;
    }

 /// <summary>Writes a 16-bit value at a position, typically into reserved space.</summary>
    public void WriteAt(int position, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(position), value);
    }

 /// <summary>Writes a 32-bit value at a position, typically the message length prefix.</summary>
    public void WriteAt(int position, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position), value);
    }

 /// <summary>Writes a Proud compact length: a width marker followed by the value in little-endian bytes.</summary>
 /// <remarks>
 /// The width is the smallest of 1, 2, 4 or 8 bytes whose signed range holds the value.
 /// </remarks>
    public void WriteCompactLength(long value)
    {
        if (value < CompactLimit1) { WriteByte(CompactWidth1); WriteByte((byte)value); }
        else if (value < CompactLimit2) { WriteByte(CompactWidth2); WriteUInt16((ushort)value); }
        else if (value < CompactLimit4) { WriteByte(CompactWidth4); WriteUInt32((uint)value); }
        else { WriteByte(CompactWidth8); WriteUInt64((ulong)value); }
    }

 /// <summary>
 /// Writes a compact-length byte array body: the length prefix, a 16-bit self length, then the payload.
 /// </summary>
 /// <param name="writePayload">Writes everything after the 16-bit self length, including the message tag and fields.</param>
    public void WriteCompactBABody(Action<PacketWriter> writePayload)
    {
        using var tmp = new PacketWriter(BodyScratchCapacity);
        writePayload(tmp);
        var payload = tmp.GetWrittenSpan();
        int selfLen = SelfLengthBytes + payload.Length;
        WriteCompactLength(selfLen);
        WriteUInt16((ushort)selfLen);
        WriteBytes(payload);
    }

 /// <summary>Gets the written bytes as a span.</summary>
    public ReadOnlySpan<byte> GetWrittenSpan() => _buffer.AsSpan(0, _position);

 /// <summary>Gets the written bytes as memory.</summary>
    public ReadOnlyMemory<byte> GetWrittenMemory() => _buffer.AsMemory(0, _position);

 /// <summary>Copies the written bytes into a new array.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();

 /// <summary>Resets the writer for reuse.</summary>
    public void Reset()
    {
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int additionalBytes)
    {
        var required = _position + additionalBytes;
        if (required <= _buffer.Length)
            return;

        Grow(required);
    }

    private void Grow(int minimumRequired)
    {
        var newSize = Math.Max(_buffer.Length * 2, minimumRequired);
        if (newSize > MaxCapacity)
        {
            if (minimumRequired > MaxCapacity)
                throw new InvalidOperationException($"Packet size exceeds maximum of {MaxCapacity} bytes");
            newSize = MaxCapacity;
        }

        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _position).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null!;
    }
}
