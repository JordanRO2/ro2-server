using RO2.Parties.Domain;

namespace RO2.Parties.Application;

/// <summary>Maps the effects of a party decision onto <see cref="IPartyNotifier"/>, one at a time and in order.</summary>
public static class PartyEffectApplier
{
    /// <summary>Delivers <paramref name="effects"/> through <paramref name="notifier"/>, awaiting each send before the next.</summary>
    public static async Task ApplyAsync(IReadOnlyList<PartyEffect> effects, IPartyNotifier notifier)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case SendToMember e: await notifier.SendToMemberAsync(e.Gid, e.Message); break;
                case SendToParty e: await notifier.SendToPartyAsync(e.Message, null); break;
                case SendToPartyExcept e: await notifier.SendToPartyAsync(e.Message, e.ExceptGid); break;
                case SendRosterCreate e: await notifier.SendRosterAsync(true, e.OnlyGid); break;
                case SendRosterUpdate: await notifier.SendRosterAsync(false, null); break;
                case SendJoinDelta e: await notifier.SendJoinDeltaAsync(e.JoinerGid); break;
                case SendDestroy e: await notifier.SendDestroyAsync(e.Copies, e.Recipients); break;
                case SendLayoff e: await notifier.SendLayoffAsync(e.Gid); break;
                case ScheduleLockSweep: notifier.ScheduleLockSweep(); break;
                default: throw new ArgumentOutOfRangeException(nameof(effects), effect, "Unknown party effect.");
            }
        }
    }
}

/// <summary>Reads the decision inputs (who is online, the known levels) out of an <see cref="IMemberDirectory"/>.</summary>
public static class MemberDirectoryExtensions
{
    /// <summary>The GIDs among <paramref name="gids"/> that are online.</summary>
    public static IReadOnlySet<ulong> OnlineAmong(this IMemberDirectory directory, IEnumerable<ulong> gids)
        => gids.Where(directory.IsOnline).ToHashSet();

    /// <summary>The known level of each GID in <paramref name="gids"/>.</summary>
    public static IReadOnlyDictionary<ulong, byte> LevelsOf(this IMemberDirectory directory, IEnumerable<ulong> gids)
    {
        Dictionary<ulong, byte> levels = [];
        foreach (var gid in gids)
            if (directory.Snapshot(gid) is { } view) levels[gid] = view.Level;
        return levels;
    }
}
