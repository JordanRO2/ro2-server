using System.Runtime.CompilerServices;
using RO2.Server.Zone.Models;
using RO2.Combats.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host facade over one <see cref="ItemSkillRepeatWindow"/> per PlayerState: it refuses a repeated item skill
/// request. A request for any item skill that arrives within <see cref="RepeatWindow"/> of the caster's last
/// answered request is answered only with a cancel notice, and nothing is consumed. The window logic and its
/// lock live in <see cref="ItemSkillRepeatWindow"/> (Combat.Domain); this maps player → window through a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> (runtime only, one timestamp per login).
/// </summary>
internal static class ItemSkillRepeatGuard
{
 /// <summary>
 /// Window in which a repeated item skill request is refused.
 /// </summary>
    internal static readonly TimeSpan RepeatWindow = ItemSkillRepeatWindow.RepeatWindow;

    private static readonly ConditionalWeakTable<PlayerState, ItemSkillRepeatWindow> LastAnswered = new();

 /// <summary>
 /// Returns true when the player's last answered item skill request is less than <see cref="RepeatWindow"/>
 /// before <paramref name="nowUtc"/>. Only reads; it does not record the request.
 /// </summary>
    internal static bool IsRepeat(PlayerState player, DateTime nowUtc) =>
        LastAnswered.TryGetValue(player, out var last) && last.IsRepeat(nowUtc);

 /// <summary>
 /// Records that the player's item skill request at <paramref name="nowUtc"/> was answered. This starts the
 /// next repeat window for every item skill.
 /// </summary>
    internal static void MarkAnswered(PlayerState player, DateTime nowUtc) =>
        LastAnswered.GetValue(player, static _ => new ItemSkillRepeatWindow()).MarkAnswered(nowUtc);
}
