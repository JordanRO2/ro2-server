using System.Buffers;
using System.Buffers.Binary;

namespace RO2.Network.ProudNet;

/// <summary>
/// Encodes and decodes ProudNet frames. Frame format: magic (2 bytes), scalar length, data.
/// </summary>
public static class ProudFrameCodec
{
 /// <summary>
 /// Largest declared frame body accepted (64 KiB). Larger declared lengths are rejected with <see cref="InvalidDataException"/>, which the session turns into a disconnect.
 /// </summary>
 /// <remarks>
 /// The declared length is untrusted. This bound stops a hostile header from forcing a very large allocation or stalling the receive pipe. It matches the inflated-length cap in <c>ProudSession.MaxInflatedLength</c>.
 /// </remarks>
    public const int MaxFrameBytes = 64 * 1024;

 /// <summary>Tries to parse one complete ProudNet frame from the buffer.</summary>
 /// <returns>True when a complete frame was parsed.</returns>
    public static bool TryParseFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frameData)
    {
        frameData = ReadOnlyMemory<byte>.Empty;

        
        if (buffer.Length < 4)
            return false;

        
        Span<byte> header = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(header);

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (magic != ProudConstants.NetMagic)
        {
            
            
            throw new InvalidDataException($"Invalid ProudNet magic: 0x{magic:X4}, expected 0x{ProudConstants.NetMagic:X4}");
        }

        
        int offset = 2;
        if (!TryReadScalar(buffer, ref offset, out var dataLength))
            return false;

        
        
        if (dataLength < 0 || dataLength > MaxFrameBytes)
            throw new InvalidDataException($"ProudNet frame length {dataLength} outside 0..{MaxFrameBytes}");

        
        
        var totalSize = checked(offset + dataLength);
        if (buffer.Length < totalSize)
            return false;

        
        var frameBytes = new byte[dataLength];
        buffer.Slice(offset, dataLength).CopyTo(frameBytes);
        frameData = frameBytes;

        
        buffer = buffer.Slice(totalSize);
        return true;
    }

 /// <summary>Reads a scalar-encoded value from the buffer.</summary>
    private static bool TryReadScalar(ReadOnlySequence<byte> buffer, ref int offset, out int value)
    {
        value = 0;

        if (buffer.Length <= offset)
            return false;

        Span<byte> temp = stackalloc byte[5];
        var available = (int)Math.Min(buffer.Length - offset, 5);
        buffer.Slice(offset, available).CopyTo(temp);

        var prefix = temp[0];
        switch (prefix)
        {
            case 1:
                if (available < 2)
                    return false;
                value = temp[1];
                offset += 2;
                return true;

            case 2:
                if (available < 3)
                    return false;
                value = BinaryPrimitives.ReadInt16LittleEndian(temp[1..]);
                offset += 3;
                return true;

            case 4:
                if (available < 5)
                    return false;
                value = BinaryPrimitives.ReadInt32LittleEndian(temp[1..]);
                offset += 5;
                return true;

            default:
                
                throw new InvalidDataException($"Invalid scalar prefix: 0x{prefix:X2}");
        }
    }

 /// <summary>Writes a scalar-encoded value.</summary>
    public static void WriteScalar(Span<byte> buffer, int value, out int bytesWritten)
    {
        if (value < 128)
        {
            buffer[0] = 1;
            buffer[1] = (byte)value;
            bytesWritten = 2;
        }
        else if (value < 32768)
        {
            buffer[0] = 2;
            BinaryPrimitives.WriteInt16LittleEndian(buffer[1..], (short)value);
            bytesWritten = 3;
        }
        else
        {
            buffer[0] = 4;
            BinaryPrimitives.WriteInt32LittleEndian(buffer[1..], value);
            bytesWritten = 5;
        }
    }

 /// <summary>Encodes data as a ProudNet frame.</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> data)
    {
        
        int scalarSize = data.Length < 128 ? 2 : (data.Length < 32768 ? 3 : 5);
        var frameSize = 2 + scalarSize + data.Length;

        var frame = new byte[frameSize];

        
        BinaryPrimitives.WriteUInt16LittleEndian(frame, ProudConstants.NetMagic);

        
        WriteScalar(frame.AsSpan(2), data.Length, out _);

        
        data.CopyTo(frame.AsSpan(2 + scalarSize));

        return frame;
    }

 /// <summary>Encodes an opcode and payload as a ProudNet frame.</summary>
    public static byte[] EncodeFrame(ProudCoreOpCode opcode, ReadOnlySpan<byte> payload)
    {
        var data = new byte[1 + payload.Length];
        data[0] = (byte)opcode;
        payload.CopyTo(data.AsSpan(1));
        return EncodeFrame(data);
    }
}
