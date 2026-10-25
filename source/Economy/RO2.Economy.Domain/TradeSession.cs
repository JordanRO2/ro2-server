using System.Collections.Concurrent;

namespace RO2.Economy.Domain;

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

    /// <summary>Offered inventory item instance uids, in drag order, de-duplicated.</summary>
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
/// <param name="Applied">Whether the toggle changed this side's state.</param>
/// <param name="BothLocked">Whether both sides are locked after the call.</param>
public readonly record struct TradeLockResult(bool Applied, bool BothLocked);

/// <summary>
/// A trade between two connected sessions: first a pending REQUEST (1351 sent, target not yet
/// answered), then, once the target accepts with 1352, a LIVE trade with an open window on both
/// clients. All mutation goes through the self-locking methods so the two clients (which arrive on
/// different IO threads) cannot corrupt the shared offer state. Pure state machine: no player object, no
/// socket, no I/O.
/// </summary>
public sealed class TradeSession
{
    /// <summary>Identifies this trade in logs (the swap's failure log names it).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The initiator (the 1351 sender).</summary>
    public uint SessionA { get; }

    /// <summary>The target (the one prompted with 2382).</summary>
    public uint SessionB { get; }

    /// <summary>PC actor handle for side A (low byte 0x11), as the clients know it. 2383 names
    /// them in this [initiator][target] order and the client uses the order to decide which half of
    /// the window is its own.</summary>
    public uint HandleA { get; }

    /// <summary>PC actor handle for side B (low byte 0x11); see <see cref="HandleA"/>.</summary>
    public uint HandleB { get; }

    /// <summary>The initiator's offer.</summary>
    public TradeOffer OfferA { get; } = new();

    /// <summary>The target's offer.</summary>
    public TradeOffer OfferB { get; } = new();

    /// <summary>True once the target accepted (1352) and the window is open on both sides.</summary>
    public bool Accepted { get; private set; }

    /// <summary>Latched exactly once, under the session gate, by the confirm that completes the trade,
    /// so the item/gold swap runs a single time no matter which side confirms last.</summary>
    public bool Completing { get; private set; }

    private readonly Lock _gate = new();

    // Offered quantity per uid, lock-free like the table this replaced; kept across a removal, as before.
    private readonly ConcurrentDictionary<ulong, int> _quantities = new();

    /// <summary>Creates a pending request between two sessions.</summary>
    /// <param name="sessionA">The initiator's session id.</param>
    /// <param name="sessionB">The target's session id.</param>
    /// <param name="handleA">The initiator's PC actor handle.</param>
    /// <param name="handleB">The target's PC actor handle.</param>
    public TradeSession(uint sessionA, uint sessionB, uint handleA, uint handleB)
    {
        SessionA = sessionA;
        SessionB = sessionB;
        HandleA = handleA;
        HandleB = handleB;
    }

    /// <summary>Marks the request accepted (the window is open). Called by the registry that opens the trade.</summary>
    public void MarkAccepted() => Accepted = true;

    /// <summary>Whether <paramref name="sessionId"/> is one of the two sides.</summary>
    /// <param name="sessionId">The session to test.</param>
    public bool Has(uint sessionId) => sessionId == SessionA || sessionId == SessionB;

    /// <summary>The other side's session id.</summary>
    /// <param name="sessionId">One side's session id.</param>
    public uint Other(uint sessionId) => sessionId == SessionA ? SessionB : SessionA;

    /// <summary>The offer of <paramref name="sessionId"/>'s side.</summary>
    /// <param name="sessionId">One side's session id.</param>
    public TradeOffer OfferFor(uint sessionId) => sessionId == SessionA ? OfferA : OfferB;

    /// <summary>The PC actor handle of <paramref name="sessionId"/>'s side.</summary>
    /// <param name="sessionId">One side's session id.</param>
    public uint HandleFor(uint sessionId) => sessionId == SessionA ? HandleA : HandleB;

    /// <summary>The quantity recorded for an offered uid, or null when none was recorded (read lock-free).</summary>
    /// <param name="uid">The offered item instance uid.</param>
    public int? QuantityOf(ulong uid) => _quantities.TryGetValue(uid, out var q) ? q : null;

    /// <summary>Records, or replaces, the offered quantity for an item uid in this trade.</summary>
    /// <param name="uid">The offered item instance uid.</param>
    /// <param name="quantity">The offered quantity.</param>
    public void RecordQuantity(ulong uid, int quantity) => _quantities[uid] = quantity;

    /// <summary>Whether this side has locked its offer (read under the gate).</summary>
    /// <param name="sessionId">One side's session id.</param>
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
    /// <param name="sessionId">The offering side's session id.</param>
    /// <param name="uid">The offered item instance uid.</param>
    /// <param name="quantity">The offered quantity.</param>
    /// <param name="snapshot">The item as the window shows it, or null.</param>
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
            RecordQuantity(uid, quantity);
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
    /// <param name="sessionId">The withdrawing side's session id.</param>
    /// <param name="uid">The item instance uid to withdraw.</param>
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
    /// <param name="sessionId">The offering side's session id.</param>
    /// <param name="uid">The offered item instance uid.</param>
    public TradeOfferSnapshot? SnapshotOf(uint sessionId, ulong uid)
    {
        lock (_gate) return OfferFor(sessionId).Snapshots.TryGetValue(uid, out var s) ? s : null;
    }

    /// <summary>
    /// Set this side's gold offer. A locked side is refused. Writing the unchanged amount is still
    /// <see cref="TradeOfferChange.Applied"/>: the client sends a 1355 with the current amount right
    /// before every 1356 lock, and retail echoes it (2386 before each first 2387).
    /// </summary>
    /// <param name="sessionId">The offering side's session id.</param>
    /// <param name="gold">The offered gold (a negative amount is stored as 0).</param>
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
    /// <param name="sessionId">The toggling side's session id.</param>
    /// <param name="locked">True to lock, false to unlock.</param>
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
    /// <param name="sessionId">The confirming side's session id.</param>
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
    /// <param name="sessionId">The side whose offer to read.</param>
    public ulong[] SnapshotItems(uint sessionId)
    {
        lock (_gate) return OfferFor(sessionId).ItemUids.ToArray();
    }
}
