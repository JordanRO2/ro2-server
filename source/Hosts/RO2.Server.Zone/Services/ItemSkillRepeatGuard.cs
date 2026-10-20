using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Refuses a repeated item skill request. A request for any item skill that arrives within
/// <see cref="RepeatWindow"/> of the caster's last answered request is answered only with a cancel notice, and
/// nothing is consumed.
/// </summary>
/// <remarks>
/// <para>
/// The window runs from the last answered request. A refused request does not move it, and neither does a
/// request refused for another reason, since the answer is recorded only on the answered path.
/// </para>
/// <para>
/// The state is runtime only, one timestamp per player state (one login).
/// </para>
/// </remarks>
internal static class ItemSkillRepeatGuard
{
 /// <summary>
 /// Window in which a repeated item skill request is refused.
 /// </summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(1000);

 /// <summary>The time of a player's last answered item skill request. Guarded by its own lock.</summary>
    private sealed class LastAnswer
    {
        internal readonly Lock Gate = new();
        internal DateTime At;
    }

    private static readonly ConditionalWeakTable<PlayerState, LastAnswer> LastAnswered = new();

 /// <summary>
 /// Returns true when the player's last answered item skill request is less than <see cref="RepeatWindow"/>
 /// before <paramref name="nowUtc"/>. Only reads; it does not record the request.
 /// </summary>
    internal static bool IsRepeat(PlayerState player, DateTime nowUtc)
    {
        if (!LastAnswered.TryGetValue(player, out var last)) return false;
        lock (last.Gate)
        {
            return nowUtc >= last.At && nowUtc - last.At < RepeatWindow;
        }
    }

 /// <summary>
 /// Records that the player's item skill request at <paramref name="nowUtc"/> was answered. This starts the
 /// next repeat window for every item skill.
 /// </summary>
    internal static void MarkAnswered(PlayerState player, DateTime nowUtc)
    {
        var last = LastAnswered.GetValue(player, static _ => new LastAnswer());
        lock (last.Gate) last.At = nowUtc;
    }
}
