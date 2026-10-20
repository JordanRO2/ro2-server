using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Answers to the job-mastery NPC dialog: 2987 AnsJobMasteryStarter, 2988 AnsJobMasteryTraining and 2989
/// AnsJobMasteryUpgrade, sent for the dialog buttons 1665, 1666 and 1667.
/// </summary>
/// <remarks>
/// Each request carries the NPC handle. Every answer has the same 10-byte body: a u32 length of 6, a u16 result code and
/// the NPC handle echoed back. This server keeps no job-mastery state, so every answer is a refusal, and the result code
/// selects the matching client message.
/// </remarks>
public static class JobMasteryPackets
{
 /// <summary>Length value that opens every 2987, 2988 and 2989 body (the size of the params struct).</summary>
    internal const uint ParamsLength = 6;

 /// <summary>Total body length of 2987, 2988 and 2989: the length field plus the params struct.</summary>
    internal const int BodyLength = 4 + (int)ParamsLength;

 /// <summary>Result code for a starter refusal ("Master level could not be started.").</summary>
    public const ushort StartFailed = 23601;

 /// <summary>Result code for a training refusal ("The condition to be a Master level up is not enough.").</summary>
    public const ushort TrainingConditionNotMet = 23802;

 /// <summary>Result code for an upgrade refusal ("The condition to be a Master level up is not enough.").</summary>
    public const ushort UpgradeConditionNotMet = 23701;

 /// <summary>
 /// Builds the 10-byte body of 2987, 2988 or 2989: a u32 length of 6, the u16 result code and the NPC handle.
 /// </summary>
    public static byte[] BuildAnswer(ushort result, uint npcHandle)
    {
        var b = new byte[BodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b, ParamsLength);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), result);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), npcHandle);
        return b;
    }

 /// <summary>
 /// Reads the NPC handle from a 1665, 1666 or 1667 body (a u32). Returns false when the body is too short.
 /// </summary>
    public static bool TryReadNpcHandle(ReadOnlySpan<byte> payload, out uint npcHandle)
    {
        npcHandle = 0;
        if (payload.Length < 4) return false;
        npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return true;
    }
}
