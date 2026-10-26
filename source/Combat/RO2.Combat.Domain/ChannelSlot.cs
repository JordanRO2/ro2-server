namespace RO2.Combats.Domain;

/// <summary>
/// The single channelled skill a caster is casting right now (0 = none). One slot per caster: an accepted 1508 of
/// a channelled skill begins it, and the caster's 1440 cancel or 1609 complete ends it. The atomic read/write and
/// the compare-and-swap end make two requests racing for the same channel safe — only one sees it active.
/// </summary>
public sealed class ChannelSlot
{
    private const uint None = 0;
    private uint _skillId;

    /// <summary>The channel in progress, or 0.</summary>
    public uint Active => Volatile.Read(ref _skillId);

    /// <summary>Records <paramref name="skillId"/> as the channel in progress, replacing any earlier one.</summary>
    public void Begin(uint skillId) => Volatile.Write(ref _skillId, skillId);

    /// <summary>
    /// Ends the channel of <paramref name="skillId"/> if it is the one in progress, returning whether it was. A
    /// request naming another skill (or 0) leaves the channel untouched. Atomic: two racers cannot both see it active.
    /// </summary>
    public bool TryEnd(uint skillId)
        => skillId != None && Interlocked.CompareExchange(ref _skillId, None, skillId) == skillId;
}
