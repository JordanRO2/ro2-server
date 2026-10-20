using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Answers to a 1412 ReqFunctionObjectControlReward: 2519 OnFuncObjectRewardResult for the kinds that pay,
/// and 2741 ReqDungeonLevelSelect for a dungeon entrance.
/// </summary>
public static class FuncObjectRewardPackets
{
 /// <summary>msgTag of 2519 OnFuncObjectRewardResult: 58360 (0xE3F8).</summary>
    internal const ushort MsgTagRewardResult = 58360;

 /// <summary>Message id of the "not enough bag space" notice (string 8807).</summary>
    public const uint BagFullMessageId = 22208;

 /// <summary>The second dword of every 2741 body.</summary>
    internal const uint DungeonLevelSelectMode = 2;

 /// <summary>Size in bytes of the 2519 body: compact-length envelope (4), msgTag (2) and 18 bytes of payload.</summary>
    private const int RewardResultBodySize = 24;

 /// <summary>The 2519 payload dword before the target handle: always 0.</summary>
    private const uint RewardResultLeadingDword = 0;

 /// <summary>Trailing bytes of the 2519 payload: always 0.</summary>
    private const int RewardResultTrailingZeros = 6;

 /// <summary>Size in bytes of the 2741 body: two dwords.</summary>
    private const int DungeonLevelSelectBodySize = 8;

 /// <summary>
 /// Builds the 2519 reward result body: <c>[01 16][u16 22][u16 58360][u32 0][u32 target][u32 systemMessageId]
 /// [6 x 00]</c>, 24 bytes.
 /// </summary>
 /// <remarks>
 /// A non-zero <paramref name="systemMessageId"/> is shown to the player.
 /// </remarks>
    public static byte[] BuildRewardResult2519(uint targetHandle, uint systemMessageId)
    {
        using var pw = new PacketWriter(RewardResultBodySize);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagRewardResult);
            p.WriteUInt32(RewardResultLeadingDword);
            p.WriteUInt32(targetHandle);
            p.WriteUInt32(systemMessageId);
            p.WriteZeros(RewardResultTrailingZeros);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2741 dungeon level-select body: <c>[u32 DungeonGroupID][u32 2]</c>, 8 bytes.
 /// </summary>
 /// <remarks>
 /// Sent right after the 2521 answer for a dungeon entrance. The client opens its level window for the group.
 /// </remarks>
    public static byte[] BuildDungeonLevelSelect2741(uint dungeonGroupId)
    {
        using var pw = new PacketWriter(DungeonLevelSelectBodySize);
        pw.WriteUInt32(dungeonGroupId);
        pw.WriteUInt32(DungeonLevelSelectMode);
        return pw.ToArray();
    }
}
