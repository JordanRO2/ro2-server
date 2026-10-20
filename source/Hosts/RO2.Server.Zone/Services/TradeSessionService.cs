using System.Collections.Concurrent;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// One player's side of a player-to-player trade: the item instance uids they have dragged into the
/// trade window, the gold they are offering, and their lock/confirm flags. Items are tracked by uid
/// only — they are NOT removed from the owner's inventory until the atomic swap at confirm time, so
/// there is nothing to "escrow" and nothing to give back on cancel. Ownership is re-validated at
/// confirm time, which is what keeps the transfer safe even if an offered item was sold/moved in the
/// meantime.
/// </summary>
public sealed class TradeOffer
{
    /// <summary>
    /// How many items one side can have in the window. The client trade window holds exactly this many
    /// per side and silently drops an eighth record. A drag made with 7 items already on the table is
    /// answered with 2384 result 1 instead of being added. Accepting an eighth would move an item the
    /// partner's window cannot show.
    /// </summary>
    public const int MaxItems = 7;

    /// <summary>Offered inventory item instance uids (<see cref="RO2.Domain.Entities.InventoryItem.Id"/>),
    /// in drag order, de-duplicated.</summary>
    public List<ulong> ItemUids { get; } = new();

    /// <summary>What each uid in <see cref="ItemUids"/> looked like when it was accepted into the window;
    /// a uid offered without one (a caller that passed none) has no entry. Touched under the
    /// session gate only.</summary>
    public Dictionary<ulong, TradeOfferSnapshot> Snapshots { get; } = new();

    /// <summary>Gold this side is offering (0 by default). Never negative.</summary>
    public long Gold { get; set; }

    /// <summary>This side pressed "lock" (1356) — the offer is frozen pending confirm.</summary>
    public bool Locked { get; set; }

    /// <summary>This side pressed "confirm" (1357) while both sides were locked.</summary>
    public bool Confirmed { get; set; }
}

/// <summary>What a request to change one side's offer did.</summary>
public enum TradeOfferChange
{
    /// <summary>The change was applied (a gold write with the unchanged amount also counts).</summary>
    Applied,

    /// <summary>The side is locked, so its offer is frozen. Retail answers nothing.</summary>
    SideLocked,

    /// <summary>The uid is already in this side's window. Retail answers 2384 result 1 to the offerer.</summary>
    AlreadyOffered,

    /// <summary>This side already has <see cref="TradeOffer.MaxItems"/> items. Retail answers 2384 result 1.</summary>
    WindowFull,

    /// <summary>The swap has started; nothing may change any more.</summary>
    Closed,
}

/// <summary>Outcome of a 1356 lock toggle: whether it was applied, and whether both sides are now locked.</summary>
public readonly record struct TradeLockResult(bool Applied, bool BothLocked);

/// <summary>
/// A trade between two connected sessions: first a pending REQUEST (1351 sent, target not yet
/// answered), then, once the target accepts with 1352, a LIVE trade with an open window on both
/// clients. All mutation goes through the self-locking methods so the two clients (which arrive on
/// different IO threads) cannot corrupt the shared offer state.
/// </summary>
public sealed class TradeSession
{
    /// <summary>Identifies this trade in logs (the swap's failure log names it).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The initiator (the 1351 sender).</summary>
    public uint SessionA { get; }

    /// <summary>The target (the one prompted with 2382).</summary>
    public uint SessionB { get; }

    /// <summary>PC actor handle for side A / B (low byte 0x11), as the clients know them. 2383 names
    /// them in this [initiator][target] order and the client uses the order to decide which half of
    /// the window is its own.</summary>
    public uint HandleA { get; }
    public uint HandleB { get; }

    public TradeOffer OfferA { get; } = new();
    public TradeOffer OfferB { get; } = new();

    /// <summary>True once the target accepted (1352) and the window is open on both sides.</summary>
    public bool Accepted { get; internal set; }

    /// <summary>Latched exactly once, under <see cref="_gate"/>, by the confirm that completes the trade,
    /// so the item/gold swap runs a single time no matter which side confirms last.</summary>
    public bool Completing { get; private set; }

    private readonly Lock _gate = new();

    public TradeSession(uint sessionA, uint sessionB, uint handleA, uint handleB)
    {
        SessionA = sessionA;
        SessionB = sessionB;
        HandleA = handleA;
        HandleB = handleB;
    }

    public bool Has(uint sessionId) => sessionId == SessionA || sessionId == SessionB;
    public uint Other(uint sessionId) => sessionId == SessionA ? SessionB : SessionA;
    public TradeOffer OfferFor(uint sessionId) => sessionId == SessionA ? OfferA : OfferB;
    public uint HandleFor(uint sessionId) => sessionId == SessionA ? HandleA : HandleB;

    /// <summary>Whether this side has locked its offer (read under the gate).</summary>
    public bool IsLocked(uint sessionId)
    {
        lock (_gate) return OfferFor(sessionId).Locked;
    }

    // WHAT AN OFFER CHANGE DOES TO THE LOCKS. It clears both CONFIRMS but neither LOCK.
    // The other side's lock is kept when one side changes its offer: when a side drags an item,
    // withdraws one or changes its gold while the partner is locked, that side's own lock that
    // follows must come back as phase 1 (both locked). Clearing the partner's lock would make that
    // second 2387 phase 0, and the client only raises the final "Do you want to trade?" dialog on
    // phase 1, so the trade would stall.
    // The confirms are cleared because a confirm is only valid for the offers it was given on. A
    // normal client cannot change an offer after a confirm (a locked side's changes are refused, and
    // a confirm needs both sides locked), so this only bites a client that skips those rules.
    private void ClearConfirms()
    {
        OfferA.Confirmed = false;
        OfferB.Confirmed = false;
    }

    /// <summary>
    /// Put <paramref name="uid"/> into this side's window with <paramref name="quantity"/> units, unless
    /// the side is locked, the uid is already there, or the window is full. The quantity is recorded
    /// only when the item is added, so a refused re-offer cannot change what the partner agreed to. The
    /// <paramref name="snapshot"/> (the item as the window shows it) is kept with the quantity and compared to the
    /// live row at the swap; TradeMoveHandler always passes one.
    /// </summary>
    public TradeOfferChange AddItem(uint sessionId, ulong uid, int quantity, TradeOfferSnapshot? snapshot = null)
    {
        lock (_gate)
        {
            if (Completing) return TradeOfferChange.Closed;
            var o = OfferFor(sessionId);
            if (o.Locked) return TradeOfferChange.SideLocked;
            if (o.ItemUids.Contains(uid)) return TradeOfferChange.AlreadyOffered;
            if (o.ItemUids.Count >= TradeOffer.MaxItems) return TradeOfferChange.WindowFull;
            o.ItemUids.Add(uid);
            TradeOfferQuantities.Set(this, uid, quantity);
            if (snapshot is { } s) o.Snapshots[uid] = s;
            else o.Snapshots.Remove(uid);
            ClearConfirms();
            return TradeOfferChange.Applied;
        }
    }

    /// <summary>
    /// Take <paramref name="uid"/> out of this side's window. Applied whatever the lock state, because
    /// the 1354 handler echoes 2385 to both windows unconditionally: refusing here would leave the item
    /// in the swap after both clients stopped showing it. Returns whether the uid was on the table.
    /// </summary>
    public bool RemoveItem(uint sessionId, ulong uid)
    {
        lock (_gate)
        {
            if (Completing) return false;
            var offer = OfferFor(sessionId);
            bool removed = offer.ItemUids.Remove(uid);
            if (removed)
            {
                offer.Snapshots.Remove(uid);
                ClearConfirms();
            }
            return removed;
        }
    }

    /// <summary>The snapshot taken when <paramref name="uid"/> was offered by this side, or null when none was
    /// recorded (read under the gate).</summary>
    public TradeOfferSnapshot? SnapshotOf(uint sessionId, ulong uid)
    {
        lock (_gate) return OfferFor(sessionId).Snapshots.TryGetValue(uid, out var s) ? s : null;
    }

    /// <summary>
    /// Set this side's gold offer. A locked side is refused. Writing the unchanged amount is still
    /// <see cref="TradeOfferChange.Applied"/>: the client sends a 1355 with the current amount right
    /// before every 1356 lock, and retail echoes it (2386 before each first 2387).
    /// </summary>
    public TradeOfferChange SetGold(uint sessionId, long gold)
    {
        lock (_gate)
        {
            if (Completing) return TradeOfferChange.Closed;
            var offer = OfferFor(sessionId);
            if (offer.Locked) return TradeOfferChange.SideLocked;
            var value = Math.Max(0, gold);
            if (offer.Gold != value)
            {
                offer.Gold = value;
                ClearConfirms();
            }
            return TradeOfferChange.Applied;
        }
    }

    /// <summary>
    /// Apply a 1356 lock toggle. A toggle that would not change this side's state is NOT applied:
    /// a lock from a side that is already locked, or an unlock from a side that is already unlocked.
    /// Retail answers neither (see TradeLockHandler). Taking the lock back clears both confirms.
    /// <see cref="TradeLockResult.BothLocked"/> is read under the same gate, after the change.
    /// </summary>
    public TradeLockResult SetLocked(uint sessionId, bool locked)
    {
        lock (_gate)
        {
            if (Completing) return new TradeLockResult(false, false);
            var o = OfferFor(sessionId);
            if (o.Locked == locked) return new TradeLockResult(false, OfferA.Locked && OfferB.Locked);
            o.Locked = locked;
            if (!locked) ClearConfirms();
            return new TradeLockResult(true, OfferA.Locked && OfferB.Locked);
        }
    }

    /// <summary>
    /// Record this side's confirm. It counts only while BOTH sides are locked: the client asks for the
    /// confirm in the dialog that the phase-1 2387 raises. Returns true ONLY for the single call that finds both
    /// sides locked and confirmed and latches <see cref="Completing"/> — that caller, and only that
    /// caller, performs the atomic swap. Every other call returns false.
    /// </summary>
    public bool ConfirmAndTryComplete(uint sessionId)
    {
        lock (_gate)
        {
            if (Completing) return false;
            if (!OfferA.Locked || !OfferB.Locked) return false;
            OfferFor(sessionId).Confirmed = true;
            if (OfferA.Confirmed && OfferB.Confirmed)
            {
                Completing = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>Snapshot of an offer's uids taken under the gate (safe to enumerate off-thread).</summary>
    public ulong[] SnapshotItems(uint sessionId)
    {
        lock (_gate) return OfferFor(sessionId).ItemUids.ToArray();
    }
}

/// <summary>
/// Registry of player-to-player trades: pending requests keyed by their initiator, and live trades
/// keyed by BOTH participant session ids (each participant maps to the same shared
/// <see cref="TradeSession"/>).
///
/// Exposed as a process-wide singleton via <see cref="Instance"/> rather than a DI service: the Zone
/// handlers are constructed by the container with a fixed 4-argument constructor and the container is
/// configured in Program.cs, so there is no seam to inject through. The state here is pure in-memory
/// cross-session bookkeeping with no dependencies, which makes a static singleton the correct,
/// dependency-free shape. The actual item/gold movement is done by the confirm handler against the
/// scoped <c>IInventoryRepository</c>/<c>ICharacterRepository</c>.
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
            request.Accepted = true;
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
