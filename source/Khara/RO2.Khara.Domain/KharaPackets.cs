using System.Buffers.Binary;

namespace RO2.Kharas.Domain;

/// <summary>Khara wire opcodes and the byte builders of the Khara messages. Pure: no session, no writer.</summary>
public static class KharaPackets
{
    public const ushort OnPcKharaPointChangeNotify = 2502;
    public const ushort AnsKharaAccept = 2574;
    public const ushort NfyKharaComplete = 2575;
    public const ushort NfyKharaReward = 2576;
    public const ushort NfyKharaInComplete = 2577;
    public const ushort AnsKharaTimerStart = 2580;

 /// <summary>Builds the 2574 AnsKharaAccept message: the Khara id and the result code.</summary>
    public static byte[] BuildAnsKharaAccept2574(uint kharaId, ushort result)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), result);
        return b;
    }

 /// <summary>Builds the 2575 NfyKharaComplete message: the Khara id.</summary>
    public static byte[] BuildNfyKharaComplete2575(uint kharaId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        return b;
    }

 /// <summary>Builds the 2576 NfyKharaReward message: the points awarded.</summary>
    public static byte[] BuildNfyKharaReward2576(uint points)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, points);
        return b;
    }

 /// <summary>
 /// Builds the 2577 NfyKharaInComplete message: the progress of one condition row of a Khara.
 /// </summary>
 /// <remarks>
 /// Layout: the Khara id, the row index, the subject, and the count.
 /// </remarks>
    public static byte[] BuildNfyKharaInComplete2577(uint kharaId, byte row, uint subject, ushort count)
    {
        var b = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        b[4] = row;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), subject);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9), count);
        return b;
    }

 /// <summary>Builds the 2580 AnsKharaTimerStart message: the Khara id, which starts the time-limit window.</summary>
    public static byte[] BuildAnsKharaTimerStart2580(uint kharaId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        return b;
    }

 /// <summary>Builds the 2502 OnPcKharaPointChangeNotify message: the character's new Khara point balance.</summary>
    public static byte[] BuildPointChange2502(uint balance)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, balance);
        return b;
    }

 /// <summary>Builds the 2858 NfyDeleteAcceptedKhara message, which removes the listed Khara ids from the accepted list.</summary>
 /// <remarks>
 /// An empty list builds the empty form, which the client ignores.
 /// </remarks>
    public static byte[] BuildNfyDeleteAcceptedKhara2858(IReadOnlyList<uint> kharaIds)
    {
        var b = new byte[4 + 2 + 4 * kharaIds.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(b.Length - 4));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)kharaIds.Count);
        for (int i = 0; i < kharaIds.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6 + 4 * i), kharaIds[i]);
        return b;
    }
}
