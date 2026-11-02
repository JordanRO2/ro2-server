using RO2.GameData.Tables;

namespace RO2.Guilds.Domain;

/// <summary>
/// Pure rules of a guild's progression: lifetime experience, the level derived from it, and the donation ("bribe")
/// rate and amounts. Each rule that changes the guild mutates the one it is given. The caller holds the guild's gate,
/// the persistence and the wire.
/// </summary>
public static class GuildProgression
{
    /// <summary>The highest donation rate a member may set, in percent.</summary>
    public const byte MaxBribePercent = 50;

    /// <summary>The divisor of the donation percentages and of the level-up credit.</summary>
    public const ulong HundredPercent = 100UL;

    /// <summary>
    /// Credits <paramref name="amount"/> to the guild's lifetime experience, saturating at <see cref="long.MaxValue"/>.
    /// Returns the experience before and after. Experience already above the cap is pulled down to it.
    /// </summary>
    public static (ulong Before, ulong After) CreditExp(Guild g, ulong amount)
    {
        const ulong Cap = long.MaxValue;
        var before = g.Exp;
        var after = before >= Cap || amount > Cap - before ? Cap : before + amount;
        g.Exp = after;
        return (before, after);
    }

    /// <summary>
    /// Credits experience donated by member <paramref name="charId"/> to the guild and to the member's own
    /// contribution, which saturates at <see cref="long.MaxValue"/>. A character that is not a member credits the
    /// guild only.
    /// </summary>
    public static (ulong Before, ulong After) CreditMemberExp(Guild g, uint charId, ulong amount)
    {
        var result = CreditExp(g, amount);
        if (g.Members.FirstOrDefault(x => x.CharId == charId) is { } m)
        {
            var credit = (long)Math.Min(amount, (ulong)long.MaxValue);
            m.ContribPoints = m.ContribPoints > long.MaxValue - credit ? long.MaxValue : m.ContribPoints + credit;
        }
        return result;
    }

    /// <summary>
    /// Stores a member's donation rate, capped at <see cref="MaxBribePercent"/>. Returns the stored rate, or null when
    /// the character is not a member. <paramref name="changed"/> tells whether the stored rate differs from before.
    /// </summary>
    public static byte? SetBribePercent(Guild g, uint charId, byte percent, out bool changed)
    {
        changed = false;
        if (g.Members.FirstOrDefault(x => x.CharId == charId) is not { } m) return null;
        var stored = Math.Min(percent, MaxBribePercent);
        if (m.BribePercent == stored) return stored;
        m.BribePercent = stored;
        changed = true;
        return stored;
    }

    /// <summary>Returns a member's donation rate, or 0 for a non-member.</summary>
    public static byte BribePercentOf(Guild g, uint charId) =>
        g.Members.FirstOrDefault(x => x.CharId == charId)?.BribePercent ?? 0;

    /// <summary>
    /// The level-up credit for one level: <paramref name="expToCompleteLevel"/> (the <c>Base_Exp_Next</c> of the level
    /// just completed) divided by 100, rounded down.
    /// </summary>
    public static ulong LevelUpBribeAmount(ulong expToCompleteLevel) => expToCompleteLevel / HundredPercent;

    /// <summary>The donated share of a quest prize: <c>floor(prizeExp × percent / 100)</c>.</summary>
    public static ulong HuntingBribeAmount(ulong prizeExp, byte percent) => prizeExp * percent / HundredPercent;

    /// <summary>
    /// Returns the guild level and the experience within it for a guild with <paramref name="lifetimeExp"/> total
    /// experience. The level is capped at the table's last level. A new guild is level 1 with 0 experience. Level 0 is
    /// returned only when the table cannot be read.
    /// </summary>
    public static (byte Level, ulong Exp) GuildLevelOf(ulong lifetimeExp, IReadOnlyList<GuildExpTable>? table)
    {
        if (table is not { Count: > 0 }) return (0, lifetimeExp);
        var rows = table.OrderBy(r => r.Guild_Base_Level).ToArray();
        var rest = lifetimeExp;
        var i = 0;
        while (i < rows.Length - 1 && rows[i].Guild_Base_Exp_Next > 0 && rest >= rows[i].Guild_Base_Exp_Next)
        {
            rest -= rows[i].Guild_Base_Exp_Next;
            i++;
        }
        return (rows[i].Guild_Base_Level, rest);
    }
}
