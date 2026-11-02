namespace RO2.Guilds.Domain;

/// <summary>
/// Pure rules of a guild's zeny funds: deposit, withdrawal and return. Each rule mutates the guild it is given and
/// reports the funds after. The caller holds the guild's gate, the persistence, the undo on a failed write and the
/// wire. The storage limit is a parameter, read by the caller from the configuration.
/// </summary>
public static class GuildFundsRules
{
    /// <summary>
    /// Applies a deposit to the funds. The deposit is accepted when the result stays at or under
    /// <paramref name="limit"/>. A negative amount is refused. Funds already above the limit refuse every deposit,
    /// a zero deposit too.
    /// </summary>
    public static bool TryAddFunds(Guild g, long amount, long limit, out long after)
    {
        if (amount < 0 || g.Gold > limit || amount > limit - g.Gold)
        {
            after = g.Gold;
            return false;
        }
        g.Gold += amount;
        after = g.Gold;
        return true;
    }

    /// <summary>
    /// Applies a withdrawal to the funds. The withdrawal is accepted when the guild holds at least
    /// <paramref name="amount"/>. A negative amount is refused.
    /// </summary>
    public static bool TryTakeFunds(Guild g, long amount, out long after)
    {
        if (amount < 0 || amount > g.Gold)
        {
            after = g.Gold;
            return false;
        }
        g.Gold -= amount;
        after = g.Gold;
        return true;
    }

    /// <summary>
    /// Returns zeny to the funds with no limit check, saturating at <see cref="long.MaxValue"/>. A negative amount
    /// wraps <c>long.MaxValue - amount</c>, so it also saturates: kept as it was.
    /// </summary>
    public static long ReturnFunds(Guild g, long amount)
    {
        g.Gold = g.Gold > long.MaxValue - amount ? long.MaxValue : g.Gold + amount;
        return g.Gold;
    }
}
