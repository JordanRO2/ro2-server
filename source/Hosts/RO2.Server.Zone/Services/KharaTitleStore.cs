using System.Collections.Concurrent;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Kharas.Domain;
using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The Zone side of a character's Khara titles: the rule <see cref="Decide"/> that answers a 1438
/// ReqKharaTitleSelect from the stored <see cref="KharaTitleDocument"/>, the published-titles cache and the
/// record writers. The document I/O (collection <c>character_khara_title</c>) is done by the injected
/// <see cref="IKharaTitleRepository"/>; the play-clock axis its instants are on is added here.
/// </summary>
/// <remarks>The rule and its documentation are <see cref="KharaTitleRules"/> (RO2.Khara.Domain); the members below that
/// share its names forward to it.</remarks>
public sealed class KharaTitleStore
{
    internal const uint TitleTimerMs = KharaTitleRules.TitleTimerMs;
    internal const uint CooldownMs = KharaTitleRules.CooldownMs;
    internal const uint ResultRefused = KharaTitleRules.ResultRefused;
    internal const uint ResultOk = KharaTitleRules.ResultOk;
    internal const uint SlotNotApplied = KharaTitleRules.SlotNotApplied;

    /// <summary>Bytes of the 2310 head's title block (body[143..160]) after the A timer and title A, left 0: fields
    /// there that this server does not model.</summary>
    private const int HeadTitleUnmodeledBytes = 10;

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

    internal static IReadOnlySet<uint> TitleGrantingKharas(IReadOnlyList<KharaInfo> rows) => KharaTitleRules.TitleGrantingKharas(rows);

    internal static bool MayWear(uint kharaId, IReadOnlyCollection<uint> completed, IReadOnlySet<uint> titleKharas)
        => KharaTitleRules.MayWear(kharaId, completed, titleKharas);

    internal static KharaTitleDecision Decide(KharaTitleState state, uint aReq, uint bReq, DateTime nowUtc, Func<uint, bool> mayWear)
        => KharaTitleRules.Decide(state, aReq, bReq, nowUtc, mayWear);

    internal static KharaTitleDecision SaveFailed(KharaTitleState previous, DateTime nowUtc)
        => KharaTitleRules.SaveFailed(previous, nowUtc);

    internal static uint TitleOf(uint kharaId, IReadOnlyList<KharaInfo> rows) => KharaTitleRules.TitleOf(kharaId, rows);

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
    /// (<see cref="KharaTitleRules.TimerLeft"/> at <see cref="KharaTitleClock.PlayNow"/> of <paramref name="nowUtc"/>, 0 when none
    /// runs), u32 title A, then 10 bytes left 0: this server does not model the other fields there. Slot B,
    /// body[185..188], is <see cref="PublishedFor"/>'s TitleB.
    /// </summary>
    internal static void WriteHeadTitleA(PacketWriter writer, uint characterId, DateTime nowUtc)
    {
        var t = PublishedFor(characterId);
        writer.WriteUInt32(t is { } p                                                 // body[143..146] A timer left
            ? KharaTitleRules.TimerLeft(p.ATimerStartUtc, KharaTitleClock.PlayNow(characterId, nowUtc))
            : 0u);
        writer.WriteUInt32(t?.TitleA ?? 0u);                                          // body[147..150] title A
        writer.WriteZeros(HeadTitleUnmodeledBytes);                                  // body[151..160]
    }
}

/// <summary>A character's worn titles as the records carry them: Khara_Info.Reward_Title_Id of slot A and of slot B
/// (0 = none), and when the A timer last started (an instant on the character's play clock, <see cref="KharaTitleClock"/>).</summary>
internal readonly record struct PublishedTitles(uint TitleA, uint TitleB, DateTime? ATimerStartUtc);
