using RO2.Domain.Enums;

namespace RO2.Domain.Entities;

/// <summary>
/// Character quest progress domain entity
/// </summary>
public class CharacterQuest
{
    public ulong Id { get; set; }
    public uint CharacterId { get; set; }
    public uint QuestId { get; set; }
    public QuestStatus Status { get; set; }
    public string? ProgressJson { get; set; }

 /// <summary>Whether the player pinned this quest in the on-screen tracker box ("lock/focus").
 /// Persisted so it survives relog; sent on login via 2779 AnsLoadQuestTraceList, updated by
 /// 1540 ReqSaveQuestTraceList. NOT part of the 2310 (the tracker set is a separate packet flow).</summary>
    public bool IsTraced { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
