using System.Buffers.Binary;

namespace RO2.Server.Zone.Services;

/// <summary>Wire builder for 2944 / 2945; the pure chain rules live in <c>RO2.Rewards.Domain.PlayTimeRewardChain</c>.</summary>
public static class PlayTimeRewardPackets
{
 /// <summary>
 /// Body of 2944 AnsPlayTimeReward and 2945 AnsPlayTimeRewardGetItem: fixed 10 bytes,
 /// <c>[u16 result][u32 slot][u32 seconds]</c>.
 /// </summary>
    public static byte[] BuildAnswer(ushort result, uint slot, uint seconds)
    {
        var body = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), slot);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), seconds);
        return body;
    }
}
