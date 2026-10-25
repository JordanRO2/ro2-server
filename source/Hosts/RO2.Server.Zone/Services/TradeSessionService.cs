using System.Collections.Concurrent;
using RO2.Economy.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Registry of player-to-player trades: pending requests keyed by their initiator, and live trades
/// keyed by BOTH participant session ids (each participant maps to the same shared
/// <see cref="TradeSession"/>).
///
/// Exposed as a process-wide singleton via <see cref="Instance"/> rather than a DI service: the Zone
/// handlers are constructed by the container with a fixed 4-argument constructor and the container is
/// configured in Program.cs, so there is no seam to inject through. The state here is pure in-memory
/// cross-session bookkeeping with no dependencies, which makes a static singleton the correct,
/// dependency-free shape. The offer state machine itself is the pure <see cref="TradeSession"/> in the economy
/// domain; the actual item/gold movement is the economy application's trade service, driven by the confirm handler.
/// </summary>
/// <remarks>
/// WHY REQUESTS AND LIVE TRADES ARE KEPT APART. A player can have one outgoing request and
/// several incoming ones, and only a live trade makes a player busy:
/// <list type="bullet">
/// <item>a 1351 sent while the sender's previous request is still unanswered gets 2381 result 0 (or
/// result 10 when the target is playing the Illusion Pyramid, which TradeOpenHandler refuses for 3 s
/// after the target's last 1616-1619, see IllusionPyramidRunStore.RecentlyUsed), while a 1351 sent
/// from inside a live trade is refused with result 14;</item>
/// <item>a target already holding an unanswered 2382 prompt can be prompted again (even by a
/// different initiator), but no 2382 reaches a client whose trade window is open.</item>
/// </list>
/// The old single map, where a request and a live trade were the same entry, let a second request
/// silently cancel a live trade.
/// </remarks>
public sealed class TradeSessionService
{
    public static readonly TradeSessionService Instance = new();

    /// <summary>Mask of the actor type tag in the low byte of a PC actor handle.</summary>
    private const uint ActorHandleTagMask = 0xFF;

    /// <summary>Bit position of the session id in a PC actor handle: <c>(sessionId &lt;&lt; 8) | tag</c>.</summary>
    private const int ActorHandleSessionShift = 8;

    private readonly Lock _gate = new();

    // Live trades (window open), under both participants' session ids.
    private readonly ConcurrentDictionary<uint, TradeSession> _sessions = new();

    // Pending requests, under the initiator's session id: one outgoing request per player.
    private readonly ConcurrentDictionary<uint, TradeSession> _requests = new();

    /// <summary>The LIVE trade a session is part of, or null.</summary>
    public TradeSession? Get(uint sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    /// <summary>
    /// The live trade a session is part of, provided BOTH participants are still online. A trade whose
    /// other side has left is dropped here, so a partner who disconnected mid-trade cannot leave the
    /// remaining player looking busy to every later request.
    /// </summary>
    public TradeSession? GetLive(uint sessionId, Func<uint, bool> isOnline)
    {
        var s = Get(sessionId);
        if (s == null) return null;
        if (isOnline(s.SessionA) && isOnline(s.SessionB)) return s;
        Cancel(sessionId);
        return null;
    }

    /// <summary>The initiator's pending request, or null.</summary>
    public TradeSession? RequestFrom(uint initiator) =>
        _requests.TryGetValue(initiator, out var s) ? s : null;

    /// <summary>
    /// Record a new request from <paramref name="initiator"/> to <paramref name="target"/>, replacing
    /// the initiator's previous one. Requests whose initiator or target has gone offline are swept on
    /// the way, so a player who logs out with a request pending does not leave it behind.
    /// </summary>
    public TradeSession Request(uint initiator, uint target, uint initiatorHandle, uint targetHandle,
        Func<uint, bool> isOnline)
    {
        lock (_gate)
        {
            foreach (var (key, pending) in _requests)
            {
                if (!isOnline(pending.SessionA) || !isOnline(pending.SessionB))
                    _requests.TryRemove(key, out _);
            }
            var s = new TradeSession(initiator, target, initiatorHandle, targetHandle);
            _requests[initiator] = s;
            return s;
        }
    }

    /// <summary>
    /// Open the window for a pending request: succeeds only if <paramref name="request"/> is still the
    /// initiator's current request and neither side is in a live trade. The accepted request leaves the
    /// pending map, and so does any other request either participant had sent: once a player is
    /// trading, an older request of theirs no longer describes anything they can do.
    /// </summary>
    public bool TryAccept(TradeSession request)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(request.SessionA, out var current) || !ReferenceEquals(current, request))
                return false;
            if (_sessions.ContainsKey(request.SessionA) || _sessions.ContainsKey(request.SessionB))
                return false;
            _requests.TryRemove(request.SessionA, out _);
            _requests.TryRemove(request.SessionB, out _);
            request.MarkAccepted();
            _sessions[request.SessionA] = request;
            _sessions[request.SessionB] = request;
            return true;
        }
    }

    /// <summary>Drop a pending request if it is still the initiator's current one (the target refused it).</summary>
    public bool WithdrawRequest(TradeSession request)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(request.SessionA, out var current) || !ReferenceEquals(current, request))
                return false;
            _requests.TryRemove(request.SessionA, out _);
            return true;
        }
    }

    /// <summary>
    /// Remove the live trade a session belongs to (both mappings) and the session's own pending
    /// request. Returns the removed live trade, if any. Because nothing is escrowed, cancelling is
    /// simply forgetting the offer state.
    /// </summary>
    public TradeSession? Cancel(uint sessionId)
    {
        lock (_gate)
        {
            _requests.TryRemove(sessionId, out _);
            if (_sessions.TryRemove(sessionId, out var s))
            {
                _sessions.TryRemove(s.Other(sessionId), out _);
                return s;
            }
            return null;
        }
    }

    /// <summary>
    /// The online player a PC actor handle names, or null. A PC handle is <c>(sessionId &lt;&lt; 8) | 0x11</c>
    /// (<see cref="PlayerStateManager.BuildActorHandle"/>); the tag and the stored EntityId are both
    /// checked, so a stale or foreign handle never resolves to whoever holds that session number now.
    /// </summary>
    public static PlayerState? ResolvePc(PlayerStateManager players, uint handle)
    {
        if ((handle & ActorHandleTagMask) != PlayerStateManager.PcActorTag) return null;
        var p = players.GetPlayer(handle >> ActorHandleSessionShift);
        return p != null && p.EntityId == handle ? p : null;
    }
}
