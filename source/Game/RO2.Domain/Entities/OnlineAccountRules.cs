namespace RO2.Domain.Entities;

/// <summary>
/// Pure rules of the online-account registry shared by the Login and Zone processes: the heartbeat and
/// staleness windows and the owner string that identifies one Zone connection. Holds no storage.
/// </summary>
public static class OnlineAccountRules
{
 /// <summary>Interval at which a Zone connection refreshes its entry and checks for a kick request.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

 /// <summary>Age after which an entry without a refresh is treated as having no live connection.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

 /// <summary>Identifies this process in <see cref="OnlineAccountEntry.Owner"/>, so a restarted Zone does not match its previous entries.</summary>
    public static readonly string ProcessInstance = Guid.NewGuid().ToString("N");

 /// <summary>Builds the owner string for a Zone connection of this process.</summary>
    public static string OwnerFor(uint zoneSessionId) => $"{ProcessInstance}:{zoneSessionId}";

 /// <summary>
 /// Returns true and the Zone session id when <paramref name="owner"/> names a connection of this process.
 /// </summary>
    public static bool TryGetLocalSessionId(string owner, out uint zoneSessionId)
    {
        zoneSessionId = 0;
        var prefix = ProcessInstance + ":";
        return owner.StartsWith(prefix, StringComparison.Ordinal)
               && uint.TryParse(owner.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out zoneSessionId);
    }

 /// <summary>Whether <paramref name="entry"/> has a live connection at <paramref name="nowUtc"/>.</summary>
    public static bool IsLive(OnlineAccountEntry entry, DateTime nowUtc) => nowUtc - entry.SeenAt < StaleAfter;
}
