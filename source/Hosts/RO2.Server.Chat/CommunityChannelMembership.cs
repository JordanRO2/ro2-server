using System.Runtime.CompilerServices;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// Tracks which community channels (<see cref="CommunityChannels"/>) each Chat session has joined.
/// </summary>
/// <remarks>
/// A session joins every channel at login, can leave a channel with 23013, and can rejoin it with 23012. Sessions
/// that are not members of a channel do not receive its chat lines. The state is held in a weak table keyed by the
/// session, so no cleanup is needed on disconnect.
/// </remarks>
public static class CommunityChannelMembership
{
    private static readonly ConditionalWeakTable<ProudSession, HashSet<ushort>> Joined = new();

 /// <summary>Adds <paramref name="session"/> to every community channel. Called at login.</summary>
    public static void JoinAll(ProudSession session)
    {
        var set = Joined.GetValue(session, static _ => new HashSet<ushort>());
        lock (set)
        {
            set.Clear();
            foreach (var ch in CommunityChannels.All) set.Add(ch.Index);
        }
    }

 /// <summary>
 /// Adds <paramref name="session"/> to a channel. Returns false when it was already a member.
 /// </summary>
    public static bool Join(ProudSession session, ushort channelIndex)
    {
        var set = Joined.GetValue(session, static _ => new HashSet<ushort>());
        lock (set) return set.Add(channelIndex);
    }

 /// <summary>
 /// Removes <paramref name="session"/> from a channel. Returns false when it was not a member.
 /// </summary>
    public static bool Leave(ProudSession session, ushort channelIndex)
    {
        if (!Joined.TryGetValue(session, out var set)) return false;
        lock (set) return set.Remove(channelIndex);
    }

 /// <summary>
 /// Returns whether <paramref name="session"/> is a member of a channel. Returns false before the session logs in.
 /// </summary>
    public static bool IsMember(ProudSession session, ushort channelIndex)
    {
        if (!Joined.TryGetValue(session, out var set)) return false;
        lock (set) return set.Contains(channelIndex);
    }
}
