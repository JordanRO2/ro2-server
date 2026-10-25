using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Transform (Trance) messages: the refusal form of 2530 OnAckTransModeStart, 2531 OnNotifyTransModeStart and 2532 OnNotifyTransModeEnd.
/// The accepted 2530 and the charge gauge 2529 are built by their own packet classes.
/// All use the compact-BA envelope [u16 selfLen][u16 msgTag][u32 0].
/// </summary>
public static class TransModeNotifyPackets
{
 /// <summary>msgTag of 2530.</summary>
    private const ushort TagOnAckTransModeStart = 58405;

 /// <summary>msgTag of 2531.</summary>
    private const ushort TagOnNotifyTransModeStart = 58406;

 /// <summary>msgTag of 2532.</summary>
    private const ushort TagOnNotifyTransModeEnd = 58407;

 /// <summary>
 /// 2530 result for a transform request made while a transform is still running. The client shows a message and keeps the running transform.
 /// </summary>
    public const ushort ResultAlreadyTransformed = 22652;

 /// <summary>
 /// Builds a 2530 with a nonzero result: [compactLen 10][u16 10][u16 msgTag][u32 0][u16 result].
 /// </summary>
    public static byte[] BuildAckTransModeStartRefused2530(ushort result)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnAckTransModeStart);
            p.WriteUInt32(0);
            p.WriteUInt16(result);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2531 OnNotifyTransModeStart: [compactLen 17][u16 17][u16 msgTag][u32 0][u32 pcHandle][u32 transBuffSkillId][u8 grade], 19 bytes.
 /// Sent to the transformer and to nearby players.
 /// </summary>
 /// <remarks>
 /// On the local player, the client casts the guardian skill. For other actors it switches their mode and starts the guardian effect.
 /// </remarks>
    public static byte[] BuildNotifyTransModeStart2531(uint pcHandle, uint transBuffSkillId, byte grade)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnNotifyTransModeStart);
            p.WriteUInt32(0);
            p.WriteUInt32(pcHandle);
            p.WriteUInt32(transBuffSkillId);
            p.WriteByte(grade);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds 2532 OnNotifyTransModeEnd: [compactLen 12][u16 12][u16 msgTag][u32 0][u32 pcHandle], 14 bytes.
 /// Sent to the transformer and to nearby players when a transform ends. Without it, the client stays in transform mode.
 /// </summary>
    public static byte[] BuildNotifyTransModeEnd2532(uint pcHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagOnNotifyTransModeEnd);
            p.WriteUInt32(0);
            p.WriteUInt32(pcHandle);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// The (skill, grade) pair a 2531 carries for a character with the given profession and completed guide quests,
 /// or null when the profession has no row or no guide quest is complete.
 /// </summary>
 /// <remarks>
 /// The skill id comes from the profession's ProJob_Type row. The grade is the highest guide quest the character has completed.
 /// </remarks>
    public static (uint SkillId, byte Grade)? ResolveTransBuff(IEnumerable<ProJobType> professions, byte profession,
        IReadOnlyCollection<uint> completedQuestIds)
    {
        var row = professions.FirstOrDefault(r => r.ID == profession);
        if (row is null || profession == 0) return null;

        (uint Quest, uint Skill)[] grades =
        [
            (row.Guide_Quest_ID1, row.Trans_Buff_Skill_ID1),
            (row.Guide_Quest_ID2, row.Trans_Buff_Skill_ID2),
            (row.Guide_Quest_ID3, row.Trans_Buff_Skill_ID3),
        ];
        for (int g = grades.Length; g >= 1; g--)
        {
            var (quest, skill) = grades[g - 1];
            if (quest != 0 && skill != 0 && completedQuestIds.Contains(quest))
                return (skill, (byte)g);
        }
        return null;
    }
}
