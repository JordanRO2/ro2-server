namespace RO2.Quests.Domain;

/// <summary>The state dword of a 2310 quest-vector element (never 0: the client leaves such a quest unset).</summary>
public static class QuestElementState
{
    /// <summary>Element state of a quest with every objective met.</summary>
    public const uint StateMet = 1;

    /// <summary>Element state of a quest still in progress.</summary>
    public const uint StateRunning = 2;
}
