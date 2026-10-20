using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Messages sent when a quest turn-in changes a character's job: the skill-translate notifications (2481 to nearby
/// players, 2480 to the changer) and the quick-slot clear (2685) that accompanies them.
/// </summary>
/// <remarks>
/// See <c>QuestPrizeHandler.ApplyQuestJobChangeAsync</c> for the order in which the frames are sent.
/// </remarks>
internal static class JobChangePackets
{
 /// <summary>Message tag of the 2481 notification (u16 at body offset 2).</summary>
    internal const ushort MsgTagSkillTranslateNotify = 58347;

 /// <summary>Message tag of the 2480 notification (u16 at body offset 2).</summary>
    internal const ushort MsgTagSkillTranslate = 58346;

 /// <summary>
 /// Builds the 2481 OnSkillTranslateNotify body: the changer's actor handle and new Job_ID. Sent to the changer and
 /// nearby players, who play the job-change effect on the changer.
 /// </summary>
    internal static byte[] BuildSkillTranslateNotify2481(uint pcHandle, ushort jobId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillTranslateNotify);
            p.WriteUInt32(0);         
            p.WriteUInt32(pcHandle);  
            p.WriteUInt16(jobId);     
        });
        return pw.ToArray();
    }

 /// <summary>Builds the 2480 OnSkillTranslate body: the changer's actor handle and new Job_ID. Sent to the changer only.</summary>
    internal static byte[] BuildSkillTranslate2480(uint pcHandle, ushort jobId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillTranslate);
            p.WriteUInt32(pcHandle);  
            p.WriteUInt16(0);         
            p.WriteUInt16(jobId);     
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2685 NfyChangeProJobDelQuickSlot body: the quick-slot cells the client must empty after a skill reset.
 /// </summary>
 /// <remarks>
 /// The body is a u32 length, a u16 count, and then count (page, slot) byte pairs in bar order.
 /// Only skill entries (type 2) of the bar are cleared; item and other entries are kept.
 /// </remarks>
    internal static byte[] BuildDelQuickSlots2685(IReadOnlyList<(byte Page, byte Slot)> cells)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32((uint)(2 + 2 * cells.Count));   
        pw.WriteUInt16((ushort)cells.Count);
        foreach (var (page, slot) in cells)
        {
            pw.WriteByte(page);
            pw.WriteByte(slot);
        }
        return pw.ToArray();
    }
}
