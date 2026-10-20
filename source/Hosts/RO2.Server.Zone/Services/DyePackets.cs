using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>Layouts of the RMI 1575 ReqUseDyeItem request and the RMI 2835 AnsUseDyeItem answer.</summary>
/// <remarks>
/// <para>
/// Request body (25 bytes): the dye item descriptor, the target item descriptor, the 32-bit colour pick and the dye slot.
/// Each descriptor is a bag byte, a slot byte and a 64-bit item uid.
/// </para>
/// <para>
/// Answer body (28 bytes): a 16-bit result, the dye count left, both descriptors and the dye slot copied from the request,
/// and the resulting colour. A result of zero means success; any other value is a generic failure.
/// </para>
/// <para>
/// A refusal uses the same echo form with a non-zero code and a zero colour. The client does not act on the code value,
/// so the code 1 is sent.
/// </para>
/// </remarks>
public static class DyePackets
{
 /// <summary>Request body length in bytes.</summary>
    public const int RequestLength = 25;

 /// <summary>One item descriptor: bag, slot and uid.</summary>
    public const int DescriptorLength = 10;

 /// <summary>Answer body length in bytes.</summary>
    public const int AnswerLength = 28;

 /// <summary>Offset of the target descriptor in the request.</summary>
    public const int TargetDescriptorOffset = DescriptorLength;

 /// <summary>Offset of the colour pick in the request.</summary>
    public const int PickOffset = 2 * DescriptorLength;

 /// <summary>Offset of the dye slot byte in the request.</summary>
    public const int DyeSlotOffset = PickOffset + 4;

 /// <summary>Highest valid dye slot index. An item has three dye slots.</summary>
    public const byte MaxDyeSlot = 2;

 /// <summary>Refusal code sent in the answer. The client does not read its value.</summary>
    public const ushort ResultRefused = 1;

 /// <summary>The dye's uid.</summary>
    public static ulong DyeUid(ReadOnlySpan<byte> request) => BinaryPrimitives.ReadUInt64LittleEndian(request.Slice(2, 8));

 /// <summary>The target's uid.</summary>
    public static ulong TargetUid(ReadOnlySpan<byte> request)
        => BinaryPrimitives.ReadUInt64LittleEndian(request.Slice(TargetDescriptorOffset + 2, 8));

 /// <summary>The colour pick.</summary>
    public static uint Pick(ReadOnlySpan<byte> request) => BinaryPrimitives.ReadUInt32LittleEndian(request.Slice(PickOffset, 4));

 /// <summary>The dye slot.</summary>
    public static byte DyeSlot(ReadOnlySpan<byte> request) => request[DyeSlotOffset];

 /// <summary>A successful answer: the dye count left, the request's descriptors and dye slot, and the colour.</summary>
    public static byte[] Build2835Success(byte dyeCountLeft, ReadOnlySpan<byte> request, uint colour)
        => Build(0, dyeCountLeft, request, colour);

 /// <summary>A refused answer: the code, a zero count and colour, and the request's descriptors and slot.</summary>
    public static byte[] Build2835Refusal(ushort code, ReadOnlySpan<byte> request) => Build(code, 0, request, 0);

    private static byte[] Build(ushort result, byte count, ReadOnlySpan<byte> request, uint colour)
    {
        if (request.Length < RequestLength)
            throw new ArgumentException($"a 1575 body is {RequestLength} bytes, got {request.Length}", nameof(request));
        var b = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(b, result);                        
        b[2] = count;                                                              
        request.Slice(0, DescriptorLength).CopyTo(b.AsSpan(3));                    
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(13), colour);            
        request.Slice(TargetDescriptorOffset, DescriptorLength).CopyTo(b.AsSpan(17)); 
        b[27] = request[DyeSlotOffset];                                            
        return b;
    }
}
