using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Maps a stored board row to the packet row (the Domain row carries no Zone type).</summary>
internal static class RankingSnapshotRowExtensions
{
    public static RankRow ToRankRow(this RankingSnapshotRow row) =>
        new(row.Rank, row.PreviousRank, row.ClassId, row.Profession, (ulong)Math.Max(0, row.Score), row.Name, row.Guild);
}

/// <summary>
/// The ranking boards of 1654 ReqRankingList and 1655 ReqRanking: the per-period counters fed by gameplay, the roll that
/// publishes the ended period as the board at each reset instant, and the two queries. Storage is behind
/// <see cref="IRankingRepository"/>; without a registered repository (tests) this does nothing.
/// </summary>
/// <remarks>
/// A board shows the previous period. Its rows stay fixed for the whole period and change at the reset instant
/// (see <see cref="RankingSchedule"/>). The roll ranks the period that just ended, and the queries answer from that
/// published snapshot until the next roll.
/// Feeds: type 1 (Kafra transports) counts glider rides and flights, type 3 (monster hunts) counts kills that give EXP,
/// and type 4 (card collector) counts card pickups that open a new bag row. The other types have no feed on this server
/// and publish empty boards.
/// Ranking: every character with a score above 0, highest score first. Equal scores are ordered by who reached the score
/// first, then by character id. Class, profession, name and guild are read at the roll.
/// The roll: a clock started on first use sleeps until the next reset instant and publishes each fed type's board. A
/// query that finds the published board older than the current period rolls it on the spot, so a server that was down
/// across a reset catches up at its first query.
/// </remarks>
public static class RankingService
{
 /// <summary>The longest the clock sleeps at once, so a wall-clock step is noticed within it.</summary>
    private static readonly TimeSpan MaxClockWait = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<byte, RankingSnapshotDocument> Published = new();
    private static readonly SemaphoreSlim RollGate = new(1, 1);
    private static int _clockStarted;
    private static (GameDataStore Store, HashSet<uint> Ids)? _cardIds;

    internal enum Feed { None, Counter }

 /// <summary>What feeds <paramref name="rankingType"/> on this server (see the type remarks).</summary>
    internal static Feed FeedOf(byte rankingType) => rankingType switch
    {
        RankingSchedule.TypeKafraTransport or RankingSchedule.TypeMonsterHunt or RankingSchedule.TypeCardCollector
            => Feed.Counter,
        _ => Feed.None,
    };

 /// <summary>The types with a feed, the only ones the roll publishes.</summary>
    internal static readonly byte[] FedTypes =
        { RankingSchedule.TypeKafraTransport, RankingSchedule.TypeMonsterHunt, RankingSchedule.TypeCardCollector };

    
 /// <summary>
 /// Adds one to <paramref name="player"/>'s counter on <paramref name="rankingType"/> for the current period. A no-op
 /// without a database or for a type with no schedule. Storage errors are logged, not thrown.
 /// </summary>
    public static Task CountAsync(IServiceProvider services, PlayerState player, byte rankingType, ILogger logger)
        => AddAsync(services, player, rankingType, 1, logger);

 /// <summary>
 /// Type 4: <paramref name="landedCount"/> items of <paramref name="itemId"/> just reached the bag, as
 /// <paramref name="landed"/> shows the stacks now. Adds the new rows among them when the item is a card. A stack that
 /// only grew adds nothing.
 /// </summary>
 /// <remarks>
 /// Two paths land a kill's drop in the bag, and each calls this once per landing: the corpse pickup and party roll
 /// (ItemPickUpHandler.NotifyLandedAsync), and the kill's auto-loot fallback (LootService.ProcessKillDropsAsync). Other
 /// bag writes do not count, since the measure is card pickups.
 /// </remarks>
    public static Task CountCardLandingAsync(IServiceProvider services, PlayerState player, uint itemId,
        int landedCount, IReadOnlyList<InventoryItem> landed, ILogger logger)
    {
        var store = services.GetService<GameDataStore>();
        int n = store is null ? 0 : CardRowsToCount(itemId, NewRowsOf(landed, landedCount), CardIds(store));
        return n > 0 ? AddAsync(services, player, RankingSchedule.TypeCardCollector, n, logger) : Task.CompletedTask;
    }

 /// <summary>
 /// The new rows among the stacks a landing of <paramref name="landedCount"/> items wrote: the stacks that hold no more
 /// than the landed count. A planner that puts the whole count into an existing stack leaves that stack above the landed
 /// count, so only the rows opened by the landing match.
 /// </summary>
    internal static int NewRowsOf(IReadOnlyList<InventoryItem> landed, int landedCount)
        => landedCount <= 0 ? 0 : landed.Count(s => s.Count <= landedCount);

 /// <summary>What <paramref name="newRows"/> new rows of <paramref name="itemId"/> add to type 4: the rows when
 /// the item is a card, else 0.</summary>
    internal static int CardRowsToCount(uint itemId, int newRows, IReadOnlySet<uint> cardIds)
        => newRows > 0 && cardIds.Contains(itemId) ? newRows : 0;

    private static async Task AddAsync(IServiceProvider services, PlayerState player, byte rankingType, int amount,
        ILogger logger)
    {
        var repo = services.GetService<IRankingRepository>();
        var store = services.GetService<GameDataStore>();
        if (repo is null || store is null || amount <= 0 || FeedOf(rankingType) != Feed.Counter)
            return;
        EnsureClockRunning(services);
        if (RankingSchedule.ResetRowOf(store, rankingType) is not { } reset)
            return;

        var now = DateTime.UtcNow;
        var period = RankingSchedule.PeriodStart(reset, now);
        try
        {
            await repo.AddScoreAsync(rankingType, period, player.CharacterId, amount, now);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: ranking type {Type} counter could not be stored", player.Name, rankingType);
        }
    }

    
 /// <summary>
 /// The board a 1654 asks for: the first <see cref="RankingPackets.BoardSize"/> rows of the published snapshot,
 /// keeping their global ranks, restricted to the class or profession the filter names. Empty for a type with
 /// no feed, no ranked character, or a snapshot that cannot be read.
 /// </summary>
    public static async Task<IReadOnlyList<RankRow>> GetBoardAsync(IServiceProvider services, byte rankingType,
        byte filter, ILogger logger, CancellationToken ct)
    {
        var snapshot = await GetPublishedAsync(services, rankingType, logger, ct);
        return Board(snapshot?.Rows, filter);
    }

 /// <summary>
 /// The requester's own record for a 1655: its published row when ranked. Otherwise an unranked record with the current
 /// period's running counter (types 1, 3 and 4), or a score of 0 for a type with no feed.
 /// </summary>
    public static async Task<RankRow> GetSelfAsync(IServiceProvider services, PlayerState player, Character? character,
        byte rankingType, byte filter, ILogger logger, CancellationToken ct)
    {
        var snapshot = await GetPublishedAsync(services, rankingType, logger, ct);
        if (SelfRanked(snapshot?.Rows, player.CharacterId, filter) is { } ranked)
            return ranked;

        long score = 0;
        try
        {
            if (FeedOf(rankingType) == Feed.Counter)
                score = await RunningCountAsync(services, player.CharacterId, rankingType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: running score of ranking type {Type} could not be read", player.Name, rankingType);
        }

        
        byte classId = character is null ? player.Job : WorldEntryService.CombatJobByte(character);
        byte profession = character is null ? (byte)0 : WorldEntryService.CreationProfessionByte(character);
        return Unranked(classId, profession, score, player.Name);
    }

 /// <summary>The unranked self-record: rank 0, previous rank 0, no guild, and the given score.</summary>
    internal static RankRow Unranked(byte classId, byte profession, long score, string name)
        => new(0, 0, classId, profession, (ulong)Math.Max(0, score), name, string.Empty);

 /// <summary>
 /// The published rows a board shows. Selection 0 is every row. A class or profession selection keeps the rows of that
 /// id. Either way the first ten are shown, with their global ranks.
 /// </summary>
    internal static IReadOnlyList<RankRow> Board(IReadOnlyList<RankingSnapshotRow>? rows, byte filter)
    {
        if (rows is null || rows.Count == 0)
            return Array.Empty<RankRow>();
        var list = new List<RankRow>(RankingPackets.BoardSize);
        foreach (var row in rows)
        {
            if (!Matches(row, filter)) continue;
            list.Add(row.ToRankRow());
            if (list.Count == RankingPackets.BoardSize) break;
        }
        return list;
    }

 /// <summary>
 /// The requester's row, or null when it is not ranked on this board. Under a class or profession filter the rank is
 /// its position among the filtered rows, and the previous rank stays the global one. A character outside the filter
 /// gets null.
 /// </summary>
    internal static RankRow? SelfRanked(IReadOnlyList<RankingSnapshotRow>? rows, uint charId, byte filter)
    {
        if (rows is null) return null;
        var (selection, _) = RankingSchedule.DecodeFilter(filter);
        uint position = 0;
        foreach (var row in rows)
        {
            if (!Matches(row, filter)) continue;
            position++;
            if (row.CharId != charId) continue;
            var r = row.ToRankRow();
            return selection == 0 ? r : r with { Rank = position };
        }
        return null;
    }

    private static bool Matches(RankingSnapshotRow row, byte filter)
    {
        var (selection, filterType) = RankingSchedule.DecodeFilter(filter);
        if (selection == 0) return true;
        return filterType switch
        {
            RankingSchedule.FilterByClass => row.ClassId == selection,
            RankingSchedule.FilterByProfession => row.Profession == selection,
            _ => true,
        };
    }

    
 /// <summary>A character's standing at the roll, before ranking.</summary>
    internal readonly record struct Standing(uint CharId, long Score, DateTime ReachedUtc, string Name, byte ClassId,
        byte Profession, string Guild);

 /// <summary>
 /// Ranks <paramref name="standings"/> into a board: scores above 0 only, highest first, then the earliest reached, then
 /// the lower character id. Ranks run 1..n with no gaps. The previous rank is the character's rank in
 /// <paramref name="previous"/>, or 0 when it was not on it.
 /// </summary>
    internal static List<RankingSnapshotRow> RankRows(IEnumerable<Standing> standings,
        IReadOnlyList<RankingSnapshotRow>? previous)
    {
        var before = new Dictionary<uint, uint>();
        if (previous != null)
            foreach (var p in previous)
                before.TryAdd(p.CharId, p.Rank);

        var rows = new List<RankingSnapshotRow>();
        uint rank = 0;
        foreach (var s in standings.Where(s => s.Score > 0)
                     .OrderByDescending(s => s.Score).ThenBy(s => s.ReachedUtc).ThenBy(s => s.CharId))
        {
            rows.Add(new RankingSnapshotRow
            {
                CharId = s.CharId,
                Rank = ++rank,
                PreviousRank = before.GetValueOrDefault(s.CharId),
                ClassId = s.ClassId,
                Profession = s.Profession,
                Score = s.Score,
                Name = s.Name,
                Guild = s.Guild,
            });
        }
        return rows;
    }

 /// <summary>
 /// Starts the roll clock once per process. Resolves only singletons, so the scope that provides them may end. Does
 /// nothing without a repository (tests).
 /// </summary>
    public static void EnsureClockRunning(IServiceProvider services)
    {
        if (Volatile.Read(ref _clockStarted) != 0)
            return;
        var repo = services.GetService<IRankingRepository>();
        var store = services.GetService<GameDataStore>();
        if (repo is null || store is null)
            return;
        var guilds = services.GetRequiredService<IGuildStore>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        if (Interlocked.CompareExchange(ref _clockStarted, 1, 0) != 0)
            return;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RankingService).FullName!)
                     ?? NullLogger.Instance;
        var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(() => RunClockAsync(repo, guilds, scopes, store, logger, stopping), CancellationToken.None);
    }

    private static async Task RunClockAsync(IRankingRepository repo, IGuildStore guilds, IServiceScopeFactory scopes, GameDataStore store, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Ranking clock started (boards roll at {Reset} UTC; next {Next:u})",
            RankingSchedule.ResetTimeOfDayUtc, NextRollAfter(store, DateTime.UtcNow));
        while (!ct.IsCancellationRequested)
        {
            
            foreach (var type in FedTypes)
            {
                try { await GetPublishedCoreAsync(repo, guilds, scopes, store, type, logger, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Ranking type {Type} could not be rolled", type); }
            }

            var now = DateTime.UtcNow;
            var wait = NextRollAfter(store, now) - now;
            if (wait > MaxClockWait) wait = MaxClockWait;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try { await Task.Delay(wait + TimeSpan.FromMilliseconds(50), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

 /// <summary>The first reset instant after <paramref name="nowUtc"/> of any fed type.</summary>
    private static DateTime NextRollAfter(GameDataStore store, DateTime nowUtc)
    {
        var next = nowUtc.AddDays(1);
        foreach (var type in FedTypes)
            if (RankingSchedule.ResetRowOf(store, type) is { } reset)
            {
                var at = RankingSchedule.NextResetAfter(reset, nowUtc);
                if (at < next) next = at;
            }
        return next;
    }

    private static async Task<RankingSnapshotDocument?> GetPublishedAsync(IServiceProvider services, byte rankingType,
        ILogger logger, CancellationToken ct)
    {
        if (FeedOf(rankingType) == Feed.None)
            return null;
        var repo = services.GetService<IRankingRepository>();
        var store = services.GetService<GameDataStore>();
        if (repo is null || store is null)
            return null;
        EnsureClockRunning(services);
        var guilds = services.GetRequiredService<IGuildStore>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        try
        {
            return await GetPublishedCoreAsync(repo, guilds, scopes, store, rankingType, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            
            logger.LogWarning(ex, "Ranking type {Type}: the current board could not be read or rolled", rankingType);
            return Published.GetValueOrDefault(rankingType);
        }
    }

 /// <summary>The board published for the current period of <paramref name="rankingType"/>, rolled now if the
 /// stored one belongs to an earlier period.</summary>
    private static async Task<RankingSnapshotDocument?> GetPublishedCoreAsync(IRankingRepository repo, IGuildStore guilds, IServiceScopeFactory scopes, GameDataStore store,
        byte rankingType, ILogger logger, CancellationToken ct)
    {
        if (RankingSchedule.ResetRowOf(store, rankingType) is not { } reset)
            return null;
        var period = RankingSchedule.PeriodStart(reset, DateTime.UtcNow);
        if (Published.TryGetValue(rankingType, out var cached) && cached.PeriodStartUtc >= period)
            return cached;

        await RollGate.WaitAsync(ct);
        try
        {
            if (Published.TryGetValue(rankingType, out cached) && cached.PeriodStartUtc >= period)
                return cached;

            var latest = await repo.GetLatestSnapshotAsync(rankingType, ct);
            if (latest != null && latest.PeriodStartUtc >= period)
            {
                Published[rankingType] = latest;
                return latest;
            }

            
            using var rollScope = scopes.CreateScope();
            var characters = rollScope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            var candidate = await RollAsync(repo, characters, guilds, rankingType, reset, period, latest, ct);
            
            var rolled = await repo.PublishSnapshotAsync(candidate, ct);
            Published[rankingType] = rolled;
            logger.LogInformation("Ranking type {Type}: board for the period from {Period:u} published with {Count} ranked",
                rankingType, period, rolled.Rows.Count);
            return rolled;
        }
        finally
        {
            RollGate.Release();
        }
    }

 /// <summary>
 /// The board of the period starting at <paramref name="period"/>: the counters of the period before it, ranked
 /// against <paramref name="replaced"/> for the previous ranks.
 /// </summary>
    private static async Task<RankingSnapshotDocument> RollAsync(IRankingRepository repo, ICharacterRepository characters, IGuildStore guilds, byte rankingType,
        RankingReset reset, DateTime period, RankingSnapshotDocument? replaced, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ended = RankingSchedule.PreviousPeriodStart(reset, period);
        var docs = await repo.GetPeriodScoresAsync(rankingType, ended, ct);
        var scores = docs.Select(d => (d.CharId, d.Score, d.ReachedUtc)).ToList();

        var ids = scores.Where(s => s.Score > 0).Select(s => s.CharId).Distinct().ToList();
        IReadOnlyList<Character> rankedCharacters = ids.Count == 0
            ? Array.Empty<Character>()
            : await characters.GetByIdsAsync(ids, ct);
        var byId = rankedCharacters.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
        
        
        if (byId.Count > 0)
            await GuildService.Instance.InitAsync(guilds);

        var standings = new List<Standing>(scores.Count);
        foreach (var (charId, score, reached) in scores)
        {
            if (!byId.TryGetValue(charId, out var c)) continue; 
            standings.Add(new Standing(charId, score, reached, c.Name,
                WorldEntryService.CombatJobByte(c), WorldEntryService.CreationProfessionByte(c),
                GuildService.Instance.GetByCharId(charId)?.Name ?? string.Empty));
        }

        return new RankingSnapshotDocument
        {
            Id = SnapshotId(rankingType, period),
            Type = rankingType,
            PeriodStartUtc = period,
            RolledUtc = now,
            Rows = RankRows(standings, replaced?.Rows),
        };
    }

    
    private static async Task<long> RunningCountAsync(IServiceProvider services, uint charId, byte rankingType,
        CancellationToken ct)
    {
        var repo = services.GetService<IRankingRepository>();
        var store = services.GetService<GameDataStore>();
        if (repo is null || store is null || RankingSchedule.ResetRowOf(store, rankingType) is not { } reset)
            return 0;
        return await repo.GetRunningScoreAsync(rankingType, RankingSchedule.PeriodStart(reset, DateTime.UtcNow), charId, ct);
    }

 /// <summary>The CardInfo.json Card_IDs, built once per loaded store.</summary>
    private static HashSet<uint> CardIds(GameDataStore store)
    {
        var cached = _cardIds;
        if (cached is { } c && ReferenceEquals(c.Store, store))
            return c.Ids;
        var ids = store.CardInfo.Select(r => r.Card_ID).ToHashSet();
        _cardIds = (store, ids);
        return ids;
    }

    internal static string SnapshotId(byte rankingType, DateTime periodStartUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{rankingType}:{periodStartUtc:yyyyMMddHHmm}");
}
