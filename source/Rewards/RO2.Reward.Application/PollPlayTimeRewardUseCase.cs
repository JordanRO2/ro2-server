using RO2.GameData.Tables;
using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>Reads the slot an account's play-time progress is working toward and its remaining seconds, which answers the poll (1642).</summary>
public sealed class PollPlayTimeRewardUseCase
{
 /// <summary>The slot and the seconds left, or null when the chain has no play-time row.</summary>
    public (uint Slot, uint RemainingSeconds)? Execute(PlayTimeRewardProgress progress, Attendance[] chain,
        DateTime loginTime, DateTime nowUtc)
    {
        lock (progress.Gate)
            return PlayTimeRewardChain.Peek(progress, chain, loginTime, nowUtc);
    }
}
