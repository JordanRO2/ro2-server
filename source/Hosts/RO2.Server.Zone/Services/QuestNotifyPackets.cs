using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the quest objective-progress notifies <c>2432 OnQuestUpdateNotify</c> and <c>2424 OnQuestAddItem</c>.
/// </summary>
/// <remarks>
/// Both bodies use the compact-BA envelope: <c>[compact marker 0x01][u8 selfLen][u16 selfLen][u16 msgTag][fields...]</c>.
/// Every send goes through <see cref="PacketWriter.WriteCompactBABody"/>, which guarantees a valid compact marker as
/// the first body byte. The payload starts with the u16 msgTag, and the client's blob begins at the u16 selfLen, so
/// the first field sits at blob+4. Field values are best-effort server state.
/// </remarks>
public static class QuestNotifyPackets
{
    /// <summary>The 2432 OnQuestUpdateNotify msgTag (0xE3BB), at blob+2.</summary>
    internal const ushort MsgTagQuestUpdate = 0xE3BB; // 58299

    /// <summary>The 2424 OnQuestAddItem msgTag (0xE3DA), at blob+2.</summary>
    internal const ushort MsgTagQuestAddItem = 0xE3DA; // 58330

    /// <summary>The 2432 constant byte at blob+12, always 1.</summary>
    private const byte QuestUpdateConstantFlag = 1;

    /// <summary>The 2432 objective count at blob+17, always 1 (one objective is reported per completion).</summary>
    private const ushort QuestUpdateObjectiveCount = 1;

    /// <summary>
    /// RMI 2432 OnQuestUpdateNotify: the objective-update notify the client shows when a quest is completed.
    /// </summary>
    /// <remarks>
    /// Blob layout (19 bytes): [u16 selfLen][u16 msgTag][u32 blob+4 = 0][u32 blob+8 questId][u8 blob+12 = 1]
    /// [u32 blob+13 objectiveTargetId][u16 blob+17 count = 1]. The target id is unaligned. The objective target maps
    /// to the quest's Complete_Type_Value1 (an NPC, a monster or an item). The count is fixed at 1.
    /// </remarks>
    public static byte[] BuildQuestUpdateNotify2432(uint questId, uint objectiveTargetId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestUpdate);
            p.WriteUInt32(0);                          // blob+4  reserved
            p.WriteUInt32(questId);                    // blob+8  Quest_Info key
            p.WriteByte(QuestUpdateConstantFlag);      // blob+12
            p.WriteUInt32(objectiveTargetId);          // blob+13 objective target id
            p.WriteUInt16(QuestUpdateObjectiveCount);  // blob+17
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2424 OnQuestAddItem: the progress tick for an item-collection objective (<c>Complete_Type == 4</c>).
    /// </summary>
    /// <remarks>
    /// Blob layout (18 bytes): [u16 selfLen][u16 msgTag][u32 blob+4 = 0][u32 blob+8 questId]
    /// [u32 blob+12 itemTemplateId][u16 blob+16 count]. The count is the running total collected so far. The item
    /// template is the quest's Complete_Type_Value1.
    /// </remarks>
    public static byte[] BuildQuestAddItem2424(uint questId, uint itemTemplateId, ushort count)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagQuestAddItem);
            p.WriteUInt32(0);              // blob+4  reserved
            p.WriteUInt32(questId);        // blob+8  Quest_Info key
            p.WriteUInt32(itemTemplateId); // blob+12 objective item template id
            p.WriteUInt16(count);          // blob+16 running collected count
        });
        return pw.ToArray();
    }
}
