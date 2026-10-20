using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Holds each account's <see cref="PlayTimeRewardProgress"/>: cached for the process and persisted in MongoDB.
/// Runs a clock that credits online time, saves progress periodically and applies the daily reset.
/// </summary>
/// <remarks>
/// Progress is keyed by account, not by character, so all characters of an account share one chain across
/// character switches and sessions (<see cref="PlayTimeRewardChain"/>). The clock credits in-world accounts
/// every <see cref="CreditInterval"/> and writes them to the database every <see cref="SaveInterval"/>.
/// At the daily reset instant every in-world account is reset and sent its first slot. Offline accounts are
/// reset when they are next read. The clock starts from the 1642 handler and is restarted on each play-time
/// message in case the first start failed. It stops with the host's ApplicationStopping token.
/// </remarks>
public sealed class PlayTimeRewardStore
{
 /// <summary>Interval at which in-world accounts are credited. Held in memory only.</summary>
    public static readonly TimeSpan CreditInterval = TimeSpan.FromSeconds(1);

 /// <summary>Interval at which in-world accounts are written to the database.</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly IPlayTimeRewardRepository _repo;
    private readonly ConcurrentDictionary<uint, PlayTimeRewardProgress> ByAccount = new();
    private readonly SemaphoreSlim SaveGate = new(1, 1);
    private int _clockStarted;

    public PlayTimeRewardStore(IPlayTimeRewardRepository repo) => _repo = repo;

 /// <summary>
 /// Returns the progress of the player's account: the cached one, else the stored one, else a fresh chain.
 /// Null when the stored state cannot be read, so the caller answers nothing instead of overwriting it on the next save.
 /// </summary>
    public async Task<PlayTimeRewardProgress?> GetAsync(PlayerState player, GameDataStore store,
        ILogger logger, CancellationToken ct)
    {
        uint accountId = player.AccountId;
        if (ByAccount.TryGetValue(accountId, out var cached))
            return cached;

        PlayTimeRewardDocument? doc;
        try
        {
            doc = await _repo.GetDocumentAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: play-time reward progress of account {Account} could not be read",
                player.Name, accountId);
            return null;
        }

        var loaded = FromDocument(doc, PlayTimeRewardChain.Chain(store), player.LoginTime, DateTime.UtcNow);
        return ByAccount.GetOrAdd(accountId, loaded);
    }

 /// <summary>The progress a stored document describes, for a stint that began at <paramref name="loginUtc"/>.</summary>
    public static PlayTimeRewardProgress FromDocument(PlayTimeRewardDocument? doc, Attendance[] chain,
        DateTime loginUtc, DateTime nowUtc)
    {
        var p = PlayTimeRewardChain.NewProgress(loginUtc, nowUtc);
        if (doc is null) return p;
        p.Index = PlayTimeRewardChain.IndexOfSlot(chain, doc.SlotId);
        p.AccumulatedSeconds = Math.Max(0, doc.AccumulatedSeconds);
        p.PeriodStartUtc = DateTime.SpecifyKind(doc.PeriodStartUtc, DateTimeKind.Utc);
        return p;
    }

 /// <summary>The document to store for account <paramref name="accountId"/>. Caller holds
 /// <see cref="PlayTimeRewardProgress.Gate"/>.</summary>
    public static PlayTimeRewardDocument Snapshot(uint accountId, PlayTimeRewardProgress p, Attendance[] chain) => new()
    {
        Id = accountId,
        SlotId = chain.Length == 0 ? 0u : chain[Math.Clamp(p.Index, 0, chain.Length - 1)].ID,
        AccumulatedSeconds = p.AccumulatedSeconds,
        PeriodStartUtc = p.PeriodStartUtc,
        UpdatedUtc = DateTime.UtcNow,
    };

 /// <summary>
 /// Upserts one account's chain as it is now. A failure is logged, not thrown; the in-memory state stays
 /// correct and the next save tick writes it again.
 /// </summary>
 /// <remarks>
 /// Each write takes its snapshot inside <see cref="SaveGate"/>, so writes land in snapshot order and a
 /// stale snapshot cannot overwrite a newer claim.
 /// </remarks>
    public async Task SaveAsync(uint accountId, PlayTimeRewardProgress p, Attendance[] chain,
        ILogger logger, CancellationToken ct)
    {
        await SaveGate.WaitAsync(ct);
        try
        {
            PlayTimeRewardDocument doc;
            lock (p.Gate)
                doc = Snapshot(accountId, p, chain);
            await _repo.SaveDocumentAsync(doc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Play-time reward progress of account {Id} could not be saved", accountId);
        }
        finally
        {
            SaveGate.Release();
        }
    }

 /// <summary>Upserts the chains of <paramref name="byAccount"/> in one bulk write. Snapshots are taken
 /// inside <see cref="SaveGate"/>, as in <see cref="SaveAsync"/>.</summary>
    private async Task SaveManyAsync(IReadOnlyDictionary<uint, PlayTimeRewardProgress> byAccount,
        Attendance[] chain, ILogger logger, CancellationToken ct)
    {
        if (byAccount.Count == 0)
            return;

        await SaveGate.WaitAsync(ct);
        try
        {
            var docs = new List<PlayTimeRewardDocument>(byAccount.Count);
            foreach (var (accountId, progress) in byAccount)
            {
                PlayTimeRewardDocument doc;
                lock (progress.Gate)
                    doc = Snapshot(accountId, progress, chain);
                docs.Add(doc);
            }
            await _repo.SaveManyDocumentsAsync(docs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Play-time reward progress of {Count} account(s) could not be saved", byAccount.Count);
        }
        finally
        {
            SaveGate.Release();
        }
    }

 /// <summary>
 /// Starts the clock once per process. Resolves only singletons, so the per-message scope that provided them may end.
 /// </summary>
    public void EnsureClockRunning(IServiceProvider services)
    {
        if (Interlocked.CompareExchange(ref _clockStarted, 1, 0) != 0)
            return;

        try
        {
            var players = services.GetRequiredService<PlayerStateManager>();
            var broadcast = services.GetRequiredService<BroadcastService>();
            var store = services.GetRequiredService<GameDataStore>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PlayTimeRewardStore).FullName!);
            var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            _ = Task.Run(() => RunClockAsync(players, broadcast, store, logger, stopping), CancellationToken.None);
        }
        catch
        {
            
            Interlocked.Exchange(ref _clockStarted, 0);
            throw;
        }
    }

    private async Task RunClockAsync(PlayerStateManager players, BroadcastService broadcast, GameDataStore store,
        ILogger logger, CancellationToken ct)
    {
        var nextReset = PlayTimeRewardChain.NextResetAfter(DateTime.UtcNow);
        logger.LogInformation(
            "Play-time reward clock started (credit every {Credit}s, save every {Save}s; daily reset at {Reset} UTC, next {Next:u})",
            CreditInterval.TotalSeconds, SaveInterval.TotalSeconds, PlayTimeRewardChain.ResetTimeOfDayUtc, nextReset);

        
        var sinceSave = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            
            
            var untilReset = nextReset - DateTime.UtcNow;
            var wait = untilReset < CreditInterval ? untilReset : CreditInterval;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }

            bool resetDue = DateTime.UtcNow >= nextReset;
            bool saveDue = resetDue || sinceSave.Elapsed >= SaveInterval;
            try
            {
                await TickAsync(players, broadcast, store, logger, resetDue, saveDue, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Play-time reward clock tick failed");
            }

            if (saveDue)
                sinceSave.Restart();
            
            if (resetDue)
                nextReset = PlayTimeRewardChain.NextResetAfter(DateTime.UtcNow);
        }

        
        
        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await TickAsync(players, broadcast, store, logger, resetDue: false, save: true, bounded.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Play-time reward final save at shutdown did not complete");
        }
    }

 /// <summary>
 /// Credits every in-world account. A credit-only tick touches only cached chains; an account not yet cached is
 /// loaded at its first poll or at the next save tick. A save or reset tick also loads uncached accounts and
 /// writes each one once. A reset tick pushes the first-slot answer to every in-world session.
 /// </summary>
    private async Task TickAsync(PlayerStateManager players, BroadcastService broadcast, GameDataStore store,
        ILogger logger, bool resetDue, bool save, CancellationToken ct)
    {
        var chain = PlayTimeRewardChain.Chain(store);
        if (chain.Length == 0)
            return;

        bool load = save || resetDue;
        var online = new Dictionary<uint, PlayTimeRewardProgress>();
        int pushed = 0;
        foreach (var player in players.GetAllPlayers())
        {
            PlayTimeRewardProgress? progress;
            if (load)
                progress = await GetAsync(player, store, logger, ct);
            else if (!ByAccount.TryGetValue(player.AccountId, out progress))
                continue;
            if (progress is null)
                continue;
            online[player.AccountId] = progress;

            (uint Slot, uint RemainingSeconds)? answer;
            lock (progress.Gate)
            {
                
                answer = PlayTimeRewardChain.Peek(progress, chain, player.LoginTime, DateTime.UtcNow);
            }

            if (!resetDue || answer is not { } a)
                continue;
            var session = broadcast.GetSession(player.SessionId);
            if (session is null)
                continue;
            try
            {
                await session.SendRmiAsync(ZoneS2COpcodes.AnsPlayTimeReward,
                    PlayTimeRewardChain.BuildAnswer(PlayTimeRewardChain.ResultOk, a.Slot, a.RemainingSeconds), ct);
                pushed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                
                logger.LogDebug(ex, "Play-time reset 2944 could not be sent to session {Id}", player.SessionId);
            }
        }

        if (save)
            await SaveManyAsync(online, chain, logger, ct);

        if (resetDue)
            logger.LogInformation("Play-time reward daily reset: sent AnsPlayTimeReward (2944) to {Count} player(s)", pushed);
    }
}
