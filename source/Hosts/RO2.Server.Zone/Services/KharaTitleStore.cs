using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>The title state <see cref="KharaTitleStore.Decide"/> works on (a value, so a decision never edits a document).
/// Both instants are on the character's play clock (<see cref="KharaTitleClock"/>).</summary>
internal readonly record struct KharaTitleState(uint KharaA, uint KharaB, DateTime? ATimerStartUtc, DateTime? LastSuccessUtc)
{
    internal static KharaTitleState From(KharaTitleDocument? doc) => doc is null
        ? default
        : new KharaTitleState(doc.KharaA, doc.KharaB, doc.ATimerStartUtc, doc.LastSuccessUtc);
}

/// <summary>
/// The answer to one 1438: whether it succeeded, the 2582 u32@4 and u32@12, the state to store on success
/// (on a refusal <see cref="NewState"/> is the old state: retail changes nothing), and the two slots the 2592
/// NfyKharaTitleChange of a success carries: the request's A / B, or <see cref="KharaTitleStore.SlotNotApplied"/>
/// for a slot whose part of the request was not applied (see <see cref="KharaTitleStore.Decide"/>). A refusal sends
/// no 2592, so its slots are unused.
/// </summary>
internal readonly record struct KharaTitleDecision(bool Accepted, uint Timer, uint Result, KharaTitleState NewState,
    uint SlotA, uint SlotB);

/// <summary>
/// The Zone side of a character's Khara titles: the rule <see cref="Decide"/> that answers a 1438
/// ReqKharaTitleSelect from the stored <see cref="KharaTitleDocument"/>, the published-titles cache and the
/// record writers. The document I/O (collection <c>character_khara_title</c>) is done by the injected
/// <see cref="IKharaTitleRepository"/>; the play-clock axis its instants are on is added here.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE SERVER KEEPS IT. The 2582 answer is not an echo: its u32@4 is the remainder of a per-character timer and
/// its result refuses a B request made too soon after the last success. The timer outlives a relog: the login 2310
/// carries what is left of it at blob+143, so it is kept with the character, not with the session.
/// </para>
/// <para>
/// BOTH CLOCKS RUN ON PLAY TIME (<see cref="KharaTitleClock"/>, where the hooks live): online only, stopped while the
/// character is offline and while it is in the Colosseum (map 4109, Map_Type 3). The A timer and the 10 s since the
/// last success are differences of play instants, and the 2310 head carries the A timer's remainder on that clock
/// (<see cref="WriteHeadTitleA"/>): it stands still while the character is away, so a relog reads what the logout
/// left. A select made on map 4109 therefore leaves the A timer running and the 10 s window not started.
/// </para>
/// <para>
/// The rules:
/// <list type="bullet">
/// <item>Success (result 0), u32@4 = what is left of the A timer: 120000 when A becomes a new non-zero title, the
/// remainder when the same A is re-selected while it runs, 120000 again when the same A is re-selected after it ran
/// out. Clearing A (A = 0) does not stop the timer. With no timer running it is 0.</item>
/// <item>The A timer is a LOCK: while it runs, a non-zero A is not applied, whether it is the A already worn (a
/// re-select) or a different one, and the 2592 of the success carries 0xFFFFFFFF in slot A
/// (<see cref="SlotNotApplied"/>). Clearing A (A = 0) is always applied. The B cooldown locks B the same way: a
/// success inside it keeps the stored B and carries 0xFFFFFFFF in slot B, even with B unchanged. A request that
/// applies nothing inside the cooldown is the refusal below.</item>
/// <item>Refusal 21751: with a non-zero B stored and within 10000 ms of the character's last success, a request
/// that applies nothing (A neither cleared, changed nor restarted; while the A timer runs that is any non-zero A, an
/// identical re-select included) is refused with u32@4 = 10000 minus the ms since that success. A change of A alone
/// is free, and with B = 0 stored nothing is refused: setting B from 0 and re-sending [0][0] are granted.</item>
/// <item>Refusal 21751 with u32@4 = 10000 constant: the shape of a refusal when the 10 s window has not started to
/// run (the character is on map 4109, where the play clock stops). This server also uses that shape for one refusal
/// of its own, a title the character has not completed (or a Khara that grants no title): THIS SERVER'S GUARD.
/// Real successes name Kharas the character has completed, so a request outside that set is never one to grant.</item>
/// </list>
/// Neither 10000, 120000 nor 21751 is in any data table (Khara_Info, KHARA_Title_Info, Config_Def).
/// </para>
/// </remarks>
public sealed class KharaTitleStore
{
    /// <summary>
    /// The A title's timer: 2582 u32@4 on a select that sets a new non-zero A, 0x1D4C0 = 120000. A re-select inside
    /// a running timer answers the remainder on the play clock (see the remarks). No table holds it.
    /// </summary>
    internal const uint TitleTimerMs = 120000;

    /// <summary>
    /// The B-change cooldown after a success: the refusal u32@4 is 10000 minus the ms since the last success, and
    /// exactly 10000 in the other refusal kind. No table holds it.
    /// </summary>
    internal const uint CooldownMs = 10000;

    /// <summary>2582 u32@12 of a refused select: 0x54F7 = 21751.</summary>
    internal const uint ResultRefused = 21751;

    /// <summary>2582 u32@12 of a successful select.</summary>
    internal const uint ResultOk = 0;

    /// <summary>A 2592 slot whose part of the request was not applied: 0xFFFFFFFF (see
    /// <see cref="Decide"/>).</summary>
    internal const uint SlotNotApplied = 0xFFFFFFFF;

    /// <summary>Bytes of the 2310 head's title block (body[143..160]) after the A timer and title A, left 0: fields
    /// there that this server does not model.</summary>
    private const int HeadTitleUnmodeledBytes = 10;

    /// <summary>The NationEnable bit of the service the retail data targets (the same rule as KharaCatalog's retail
    /// block): a block whose main row carries it, or carries 0, is the one used.</summary>
    private const int RetailNationBit = 4;

    private static readonly ConditionalWeakTable<IReadOnlyList<KharaInfo>, RetailTitles> TitlesByTable = new();

    /// <summary>The title each Khara grants (<see cref="TitleOf"/>) and the set of those that grant one.</summary>
    private sealed record RetailTitles(Dictionary<uint, uint> ByKhara, HashSet<uint> Granting);

    /// <summary>The worn titles published to the synchronous record writers, by character id (see <see cref="Publish"/>).</summary>
    private static readonly ConcurrentDictionary<uint, PublishedTitles> Published = new();

    private readonly IKharaTitleRepository _repository;

    public KharaTitleStore(IKharaTitleRepository repository)
    {
        _repository = repository;
    }

    /// <summary>The character's title state, or null when it never selected a title on this server.</summary>
    public Task<KharaTitleDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        => _repository.GetAsync(characterId, ct);

    /// <summary>Stores <paramref name="state"/> as the character's title state (create on first save), with where its
    /// play clock stands now (<see cref="KharaTitleDocument.PlayClockUtc"/>), the axis the state's instants are on.</summary>
    internal Task SaveAsync(uint characterId, KharaTitleState state, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return _repository.SaveAsync(
            new KharaTitleDocument
            {
                Id = characterId,
                KharaA = state.KharaA,
                KharaB = state.KharaB,
                ATimerStartUtc = state.ATimerStartUtc,
                LastSuccessUtc = state.LastSuccessUtc,
                PlayClockUtc = KharaTitleClock.PlayNow(characterId, now),
                UpdatedUtc = now,
            }, ct);
    }

    /// <summary>
    /// Stores where the character's play clock stands (<see cref="KharaTitleClock.PlayNow"/>) into its title document,
    /// for the next login's <see cref="PreloadAsync"/>. Called at each exit from the world (1302 LogoutHandler, 1418
    /// ChangeCharacterHandler, the socket close in ZoneService) after PlayerStateManager.RemovePlayer paused the clock.
    /// A character with no title document gets none: its clock matters only once a select stores an instant on it.
    /// </summary>
    public Task SavePlayClockAsync(uint characterId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return _repository.StorePlayClockAsync(characterId, KharaTitleClock.PlayNow(characterId, now), now, ct);
    }

    /// <summary>
    /// The Khara ids that grant a title (<see cref="TitleOf"/> non-zero). Cached per loaded row list,
    /// as KharaCatalog.For.
    /// </summary>
    internal static IReadOnlySet<uint> TitleGrantingKharas(IReadOnlyList<KharaInfo> rows) => TitlesOf(rows).Granting;

    /// <summary>
    /// Whether <paramref name="kharaId"/> is a title the character may wear: a Khara it completed
    /// (<paramref name="completed"/>, KharaStateDocument.Completed) that grants a title (<paramref name="titleKharas"/>,
    /// from <see cref="TitleGrantingKharas"/>).
    /// </summary>
    internal static bool MayWear(uint kharaId, IReadOnlyCollection<uint> completed, IReadOnlySet<uint> titleKharas)
        => completed.Contains(kharaId) && titleKharas.Contains(kharaId);

    /// <summary>
    /// The answer to a 1438 asking for <paramref name="aReq"/> / <paramref name="bReq"/> at <paramref name="nowUtc"/>
    /// on <paramref name="state"/> (the rules are in the remarks). <paramref name="nowUtc"/> is an
    /// instant on the clock the state's instants are on: the handler passes the character's play clock
    /// (<see cref="KharaTitleClock.PlayNow"/>). <paramref name="mayWear"/> says whether a non-zero id is a title the
    /// character may wear (<see cref="MayWear"/>).
    /// </summary>
    internal static KharaTitleDecision Decide(KharaTitleState state, uint aReq, uint bReq, DateTime nowUtc, Func<uint, bool> mayWear)
    {
        // (a) A title the character may not wear: the T = 10000 refusal shape (see the remarks). This ownership
        // refusal is this server's own guard; the shape only borrows the standard refusal frame.
        if ((aReq != 0 && !mayWear(aReq)) || (bReq != 0 && !mayWear(bReq)))
            return new KharaTitleDecision(false, CooldownMs, ResultRefused, state, SlotNotApplied, SlotNotApplied);

        // The two locks (remarks). The A timer: while it runs no non-zero A is applied, the same one or another; a
        // clear (A = 0) always is. The B cooldown: inside 10 s of the last success, with a non-zero B stored, B is kept.
        var timerRunning = TimerLeft(state.ATimerStartUtc, nowUtc) > 0;
        uint? since = state.LastSuccessUtc is { } last ? ElapsedMs(last, nowUtc) : null;
        var cooling = state.KharaB != 0 && since < CooldownMs;
        var aApplied = aReq != state.KharaA && (aReq == 0 || !timerRunning);
        var aRestart = aReq != 0 && aReq == state.KharaA && !timerRunning;

        // (b) Inside the cooldown a request that applies nothing is refused (a B change on its own, or an identical
        // re-select). A refusal does not move the clock.
        if (cooling && !aApplied && !aRestart)
            return new KharaTitleDecision(false, CooldownMs - since.GetValueOrDefault(), ResultRefused, state,
                SlotNotApplied, SlotNotApplied);

        // (c) Success. The A timer restarts when A changes to a non-zero title, or when the same A is re-selected
        // after its window ran out; a clear leaves it running. Each 2592 slot echoes the request, or carries
        // SlotNotApplied when its lock kept that part out (a non-zero A while the timer runs; B inside the cooldown).
        var start = state.ATimerStartUtc;
        if ((aApplied && aReq != 0) || aRestart)
            start = nowUtc;
        var next = new KharaTitleState(aApplied ? aReq : state.KharaA, cooling ? state.KharaB : bReq, start, nowUtc);
        var slotA = aReq != 0 && timerRunning ? SlotNotApplied : aReq;
        var slotB = cooling ? SlotNotApplied : bReq;
        return new KharaTitleDecision(true, TimerLeft(start, nowUtc), ResultOk, next, slotA, slotB);
    }

    /// <summary>
    /// The refusal for an accepted select whose state could not be stored: <paramref name="previous"/> stays the state
    /// (so the caller publishes nothing), 2582 u32@4 is what is left of its A timer at <paramref name="nowUtc"/>, and no
    /// 2592 / 2498 follows. The answer to a failed store is this server's own; it only
    /// reuses the refusal shape (<see cref="ResultRefused"/>).
    /// </summary>
    internal static KharaTitleDecision SaveFailed(KharaTitleState previous, DateTime nowUtc)
        => new(false, TimerLeft(previous.ATimerStartUtc, nowUtc), ResultRefused, previous, SlotNotApplied, SlotNotApplied);

    /// <summary>
    /// The title a worn Khara shows: the Reward_Title_Id of the main row of the id's RETAIL block, 0 for a Khara that
    /// grants none. An id's Khara_Info rows split into blocks, each starting at a row with a non-zero Quest_Type (its
    /// main row); the retail block is the first whose main row has NationEnable 0 or bit 4 set, else the first (the
    /// rule KharaCatalog uses for the Khara definitions). The records carry these TITLE ids, not the Khara ids
    /// 1438 sends: the 2310 of a character that selected a title carries title(A) at blob+147 and title(B) at
    /// blob+185.
    /// </summary>
    /// <remarks>
    /// The block matters for some ids. Khara 410's first block (Quest_Type 1, NationEnable 123) has title 0 and its
    /// retail block (Quest_Type 4, NationEnable 4) title 155. 401-408 and 418 take the title of their retail block
    /// (401: 146, not the NationEnable-123 block's 145), and 420 / 421 grant none (their titles are on a
    /// NationEnable-2 block). Ids with no NationEnable-0/bit-4 block
    /// keep their first block's title, as the catalog keeps their definition.
    /// </remarks>
    internal static uint TitleOf(uint kharaId, IReadOnlyList<KharaInfo> rows)
        => kharaId == 0 ? 0 : TitlesOf(rows).ByKhara.GetValueOrDefault(kharaId);

    private static RetailTitles TitlesOf(IReadOnlyList<KharaInfo> rows) =>
        TitlesByTable.GetValue(rows, static r =>
        {
            // Per id, in table order: the main row of its first block, and of its retail block once found.
            var first = new Dictionary<uint, KharaInfo>();
            var retail = new Dictionary<uint, KharaInfo>();
            foreach (var row in r)
            {
                var opensBlock = first.TryAdd(row.Quest_ID, row) || row.Quest_Type != 0;
                if (opensBlock && !retail.ContainsKey(row.Quest_ID)
                    && (row.NationEnable == 0 || (row.NationEnable & RetailNationBit) != 0))
                    retail[row.Quest_ID] = row;
            }
            var byKhara = new Dictionary<uint, uint>();
            foreach (var (id, firstMain) in first)
            {
                var title = retail.TryGetValue(id, out var main) ? main.Reward_Title_Id : firstMain.Reward_Title_Id;
                if (title != 0) byKhara[id] = title;
            }
            return new RetailTitles(byKhara, [.. byKhara.Keys]);
        });

    /// <summary>
    /// Publishes <paramref name="state"/> as the character's worn titles for the 2310 head and the PC record writers,
    /// which run synchronously (the UnionMembershipStore pattern): called by <see cref="PreloadAsync"/> at login and
    /// by KharaTitleSelectHandler on every accepted 1438. A state with no title clears the entry.
    /// </summary>
    internal static void Publish(uint characterId, KharaTitleState state, IReadOnlyList<KharaInfo> rows)
    {
        var published = new PublishedTitles(TitleOf(state.KharaA, rows), TitleOf(state.KharaB, rows), state.ATimerStartUtc);
        if (published.TitleA == 0 && published.TitleB == 0 && published.ATimerStartUtc is null)
            Published.TryRemove(characterId, out _);
        else
            Published[characterId] = published;
    }

    /// <summary>
    /// Loads the character's stored title state and publishes it (<see cref="Publish"/>), and starts its play clock
    /// from where the last exit left it (<see cref="KharaTitleClock.Seed"/>, then <see cref="KharaTitleClock.Resume"/>:
    /// retail's clock resumes at the login, before the 2310, see <see cref="KharaTitleClock"/>). LoginPCHandler calls it
    /// before the world-entry burst, so the 2310 carries the titles and the paused remainder, and every later 2504 /
    /// 2464 / 2465 record of this PC carries the titles too.
    /// </summary>
    public async Task PreloadAsync(uint characterId, IReadOnlyList<KharaInfo> rows,
        CancellationToken ct = default)
    {
        var doc = await GetAsync(characterId, ct);
        var now = DateTime.UtcNow;
        KharaTitleClock.Seed(characterId, doc?.PlayClockUtc, now);
        KharaTitleClock.Resume(characterId, now);
        Publish(characterId, KharaTitleState.From(doc), rows);
    }

    /// <summary>The titles published for the character, or null when it wears none.</summary>
    internal static PublishedTitles? PublishedFor(uint characterId)
        => Published.TryGetValue(characterId, out var t) ? t : null;

    /// <summary>
    /// The 2310 head's body[143..160] (18 bytes): u32 what is left of the A title's timer on the character's play clock
    /// (<see cref="TimerLeft"/> at <see cref="KharaTitleClock.PlayNow"/> of <paramref name="nowUtc"/>, 0 when none
    /// runs), u32 title A, then 10 bytes left 0: this server does not model the other fields there. Slot B,
    /// body[185..188], is <see cref="PublishedFor"/>'s TitleB.
    /// </summary>
    internal static void WriteHeadTitleA(PacketWriter writer, uint characterId, DateTime nowUtc)
    {
        var t = PublishedFor(characterId);
        writer.WriteUInt32(t is { } p                                                 // body[143..146] A timer left
            ? TimerLeft(p.ATimerStartUtc, KharaTitleClock.PlayNow(characterId, nowUtc))
            : 0u);
        writer.WriteUInt32(t?.TitleA ?? 0u);                                          // body[147..150] title A
        writer.WriteZeros(HeadTitleUnmodeledBytes);                                  // body[151..160]
    }

    /// <summary>Ms left of an A timer started at <paramref name="start"/>, 0 when none runs (both instants on the same
    /// clock).</summary>
    internal static uint TimerLeft(DateTime? start, DateTime nowUtc)
    {
        if (start is not { } s) return 0;
        var since = ElapsedMs(s, nowUtc);
        return since >= TitleTimerMs ? 0 : TitleTimerMs - since;
    }

    /// <summary>Whole ms from <paramref name="from"/> to <paramref name="to"/>, 0 when the clock went backwards.</summary>
    private static uint ElapsedMs(DateTime from, DateTime to)
    {
        var ms = (to - from).TotalMilliseconds;
        if (ms <= 0) return 0;
        return ms >= uint.MaxValue ? uint.MaxValue : (uint)ms;
    }
}

/// <summary>A character's worn titles as the records carry them: Khara_Info.Reward_Title_Id of slot A and of slot B
/// (0 = none), and when the A timer last started (an instant on the character's play clock, <see cref="KharaTitleClock"/>).</summary>
internal readonly record struct PublishedTitles(uint TitleA, uint TitleB, DateTime? ATimerStartUtc);
