using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>The Quest_Info gate columns of one quest, as the accept gates read them.</summary>
public readonly record struct AcceptGateColumns(uint PcJob, byte PcProJob, byte JobMasterMinLevel,
    byte JobMasterMaxLevel, byte JobMasterQuest);

/// <summary>
/// Pure accept gates of a 1374 quest-accept: the Job_Master range, PC_Job and PC_ProJob (each refused with a 2431
/// code), and the start item of Start_Type 2 quests. The gates run in that order. Job_Master compares the base level
/// (or the master level, for Job_Master_Quest 1) with Min..Max, where a Max of 0 means no upper bound and Max is
/// EXCLUSIVE (unlike the level-grant rule, whose maximum is inclusive).
/// </summary>
public static class QuestAcceptRules
{
    /// <summary>Quest_Info Start_Type of a quest started from an item (Start_Type_ID is the item).</summary>
    public const byte StartFromItem = 2;

    /// <summary>2431 code: the level is outside the Job_Master range.</summary>
    public const uint ErrorJobMasterLevel = 21702;

    /// <summary>2431 code: the PC_Job mask does not include the character's class.</summary>
    public const uint ErrorJobMismatch = 21718;

    /// <summary>2431 code: PC_ProJob is not the character's profession.</summary>
    public const uint ErrorProJobMismatch = 21719;

    /// <summary>The 2431 code that refuses the accept, or null when every gate passes (Job_Master, PC_Job, PC_ProJob).</summary>
    public static uint? Refusal(AcceptGateColumns row, byte combatClass, byte profession, byte baseLevel, byte masterLevel)
    {
        byte v = row.JobMasterQuest == 1 ? masterLevel : baseLevel;
        if (v < row.JobMasterMinLevel || (row.JobMasterMaxLevel != 0 && v >= row.JobMasterMaxLevel))
            return ErrorJobMasterLevel;
        if (!JobAllows(row.PcJob, combatClass)) return ErrorJobMismatch;
        if (row.PcProJob != 0 && row.PcProJob != profession) return ErrorProJobMismatch;
        return null;
    }

    /// <summary>A PC_Job mask of 0 lets every class; otherwise bit (class - 1) must be set. A class outside 1..32
    /// (Novice 100, or 0 when unknown) has no bit.</summary>
    public static bool JobAllows(uint pcJob, byte combatClass) =>
        pcJob == 0 || (combatClass is >= 1 and <= 32 && (pcJob & (1u << (combatClass - 1))) != 0);

    /// <summary>The item a Start_Type 2 quest needs in the bag to be accepted, or 0 for any other Start_Type.</summary>
    public static uint StartItemOf(QuestData quest) => quest.Start_Type == StartFromItem ? quest.Start_Type_ID : 0;
}
