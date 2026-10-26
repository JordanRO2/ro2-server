namespace RO2.Combats.Domain;

/// <summary>
/// One caster's last answered item skill request. A request for any item skill that arrives within
/// <see cref="RepeatWindow"/> of it is a repeat (the host answers it only with a cancel notice and consumes
/// nothing). The window runs from the last answered request; a refused one does not move it, since the host
/// records only on the answered path. The host holds one instance per player (runtime only, one login).
/// </summary>
public sealed class ItemSkillRepeatWindow
{
    /// <summary>Window in which a repeated item skill request is refused (1000 ms).</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(1000);

    private readonly object _gate = new();
    private DateTime _answeredAt;
    private bool _answered;

    /// <summary>
    /// True when the player's last answered item skill request is less than <see cref="RepeatWindow"/> before
    /// <paramref name="nowUtc"/>. Only reads; it does not record the request.
    /// </summary>
    public bool IsRepeat(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _answered && nowUtc >= _answeredAt && nowUtc - _answeredAt < RepeatWindow;
        }
    }

    /// <summary>
    /// Records that the player's item skill request at <paramref name="nowUtc"/> was answered. This starts the
    /// next repeat window for every item skill.
    /// </summary>
    public void MarkAnswered(DateTime nowUtc)
    {
        lock (_gate)
        {
            _answeredAt = nowUtc;
            _answered = true;
        }
    }
}
