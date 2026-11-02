using RO2.GameData.Tables;

namespace RO2.Quests.Domain;

/// <summary>Pure pick of the JobChange_Table row a job-change quest applies. The skill reset, the wire and the save stay in the host.</summary>
public static class JobChangeSelection
{
    /// <summary>
    /// The row for <paramref name="jobId"/> (null when it is no byte): the one that names <paramref name="questId"/> as its
    /// Need_Quest_ID, else the first non-master row of the job, else the first row of the job.
    /// </summary>
    public static JobChangeTable? Pick(uint jobId, uint questId, IEnumerable<JobChangeTable> table)
    {
        if (jobId > byte.MaxValue) return null;
        return table.FirstOrDefault(j => j.Job_ID == jobId && j.Need_Quest_ID == questId)
               ?? table.FirstOrDefault(j => j.Job_ID == jobId && j.ISJobMaster == 0)
               ?? table.FirstOrDefault(j => j.Job_ID == jobId);
    }
}
