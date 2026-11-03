using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RO2.Colosseum.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// In-memory state of the Colosseum (PvP arena): a queue of registrants and a scoreboard. The queue is transient and is not persisted.
/// </summary>
/// <remarks>
/// <para>
/// Joining is a toggle: the first join request registers the player and the second cancels the registration.
/// </para>
/// <para>
/// Implemented as a static singleton outside DI. All collections are concurrent.
/// </para>
/// </remarks>
public static class ColosseumService
{
 /// <summary>
 /// Whether this server runs Colosseum matches. It is false: join requests are logged and not answered, and the Colosseum opening announcements are suppressed, because no match exists to join.
 /// </summary>
 /// <remarks>
 /// Every sender of the start notification checks this value. The schedule in <see cref="ColosseumSchedule"/> is kept for when matches are served.
 /// </remarks>
    public static bool MatchesServed => false;

 /// <summary>Default ready-state byte of a queued registrant. It is a stable non-zero "queued" marker.</summary>
    public const byte DefaultReadyState = 1;

 /// <summary>Result of toggling a queue registration.</summary>
    public enum JoinResult { Registered, Cancelled }

 /// <summary>A player waiting in the Colosseum queue.</summary>
    public sealed class Registrant
    {
        public uint SessionId { get; init; }
        public uint ActorHandle { get; init; }
        public string Name { get; init; } = string.Empty;
        public byte State = DefaultReadyState;
        public long JoinedAtTicks { get; init; }
    }

    
    private static readonly ConcurrentDictionary<uint, Registrant> _queue = new();
    
    
    private static readonly ConcurrentDictionary<string, int> _scores = new();
    
    private static int _rankingVersion;

 /// <summary>Room id sent with the party state notification. It is a fixed neutral value.</summary>
    public const uint RoomId = 0u;

 /// <summary>Toggles the queue registration of <paramref name="sessionId"/>: the first call registers and the second cancels.</summary>
    public static JoinResult Toggle(uint sessionId, uint actorHandle, string name)
    {
        if (_queue.TryRemove(sessionId, out _))
        {
            System.Threading.Interlocked.Increment(ref _rankingVersion);
            return JoinResult.Cancelled;
        }

        _queue[sessionId] = new Registrant
        {
            SessionId = sessionId,
            ActorHandle = actorHandle,
            Name = name ?? string.Empty,
            State = DefaultReadyState,
            JoinedAtTicks = System.DateTime.UtcNow.Ticks,
        };
        
        if (!string.IsNullOrEmpty(name))
            _scores.TryAdd(name, 0);
        System.Threading.Interlocked.Increment(ref _rankingVersion);
        return JoinResult.Registered;
    }

 /// <summary>Returns true if the session is in the queue.</summary>
    public static bool IsRegistered(uint sessionId) => _queue.ContainsKey(sessionId);

 /// <summary>Removes a session from the queue, for example on disconnect. Returns true if it was queued.</summary>
    public static bool Remove(uint sessionId)
    {
        if (_queue.TryRemove(sessionId, out _))
        {
            System.Threading.Interlocked.Increment(ref _rankingVersion);
            return true;
        }
        return false;
    }

 /// <summary>Returns a snapshot of the current registrants, in join order.</summary>
    public static IReadOnlyList<Registrant> GetRegistrants()
        => _queue.Values.OrderBy(r => r.JoinedAtTicks).ToList();

 /// <summary>Returns the session ids of the current registrants.</summary>
    public static IReadOnlyList<uint> GetRegisteredSessions()
        => _queue.Keys.ToList();

 /// <summary>Gets the number of registrants in the queue.</summary>
    public static int Count => _queue.Count;

 /// <summary>Returns the ready-state byte of each queued registrant.</summary>
    public static byte[] MemberStates()
        => GetRegistrants().Select(r => r.State).ToArray();

 /// <summary>Returns the ranking value sent with the ranking update.</summary>
    public static byte RankingByte()
        => ColosseumScoreboard.RankingByte(_scores.Count);

 /// <summary>Records a score for a player and updates the ranking.</summary>
    public static void RecordScore(string name, int delta = 1)
    {
        if (string.IsNullOrEmpty(name)) return;
        _scores.AddOrUpdate(name, delta, (_, cur) => cur + delta);
        System.Threading.Interlocked.Increment(ref _rankingVersion);
    }

 /// <summary>
 /// Returns the scoreboard rows: the current registrants first, then other ranked players, ordered by score from highest to lowest.
 /// </summary>
    public static IReadOnlyList<ColosseumPackets.ScoreEntry> Scoreboard()
        => ColosseumScoreboard.Order(GetRegistrants().Select(r => r.Name).ToList(), _scores)
            .Select(s => new ColosseumPackets.ScoreEntry(s.Name, s.Score)).ToList();
}
