using RO2.Guilds.Domain;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Quests.Domain;
using RO2.Domain.Entities;
using RO2.Kharas.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-side Khara progress and completion for each character. Progress is persisted through <see cref="IKharaStateRepository"/>.
/// </summary>
/// <remarks>
/// Triggers are wired from the game systems: accepting a Khara, kills, held-item changes, quest completion, item use, death,
/// revival, harvest, skill use, Guardian summons, repairs, level changes and guild joins. Each trigger sends its messages
/// through <see cref="KharaEngine"/>. The client never requests completion itself.
/// </remarks>
public static class KharaService
{
 /// <summary>Accepted-Khara state value for a Khara in progress.</summary>
    public const uint StateInProgress = KharaActiveDocument.StateInProgress;

    public const byte CompleteKillNpc = KharaRules.CompleteKillNpc;
    public const byte CompleteHoldItem = KharaRules.CompleteHoldItem;
    public const byte CompleteQuestDone = KharaRules.CompleteQuestDone;
    public const byte CompleteKharaDone = KharaRules.CompleteKharaDone;
    public const byte ConditionQuestDone = KharaRules.ConditionQuestDone;
    public const byte CompleteUseItem = KharaRules.CompleteUseItem;
    public const byte CompleteTitles = KharaRules.CompleteTitles;
    public const byte CompleteDeath = KharaRules.CompleteDeath;
    public const byte CompleteRevived = KharaRules.CompleteRevived;
    public const byte CompleteHarvest = KharaRules.CompleteHarvest;
    public const byte CompleteUseSkill = KharaRules.CompleteUseSkill;
    public const byte CompleteGuardianSummon = KharaRules.CompleteGuardianSummon;
    public const byte CompleteInGuild = KharaRules.CompleteInGuild;
    public const byte CompleteBaseLevel = KharaRules.CompleteBaseLevel;
    public const byte CompleteProfessionLevel = KharaRules.CompleteProfessionLevel;
    public const byte CompleteRepairCount = KharaRules.CompleteRepairCount;
    public const byte ActiveOnAccept = KharaRules.ActiveOnAccept;
    public const byte ActionSpendPoints = KharaRules.ActionSpendPoints;
    public const byte FailTypeTimeLimit = KharaRules.FailTypeTimeLimit;

    public static bool IsLevelRow(byte completeType) => KharaRules.IsLevelRow(completeType);
    public static uint RowSubject(KharaInfo c) => KharaRules.RowSubject(c);
    public static uint RowTarget(KharaInfo c) => KharaRules.RowTarget(c);
    public static bool IsTimed(KharaDefinition def) => KharaRules.IsTimed(def);

    private static readonly ConcurrentDictionary<uint, KharaStateDocument> Published = new();
    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> Gates = new();

    public static byte[] BuildAnsKharaAccept2574(uint kharaId, ushort result) => KharaPackets.BuildAnsKharaAccept2574(kharaId, result);
    public static byte[] BuildNfyKharaComplete2575(uint kharaId) => KharaPackets.BuildNfyKharaComplete2575(kharaId);
    public static byte[] BuildNfyKharaReward2576(uint points) => KharaPackets.BuildNfyKharaReward2576(points);
    public static byte[] BuildNfyKharaInComplete2577(uint kharaId, byte row, uint subject, ushort count)
        => KharaPackets.BuildNfyKharaInComplete2577(kharaId, row, subject, count);
    public static byte[] BuildAnsKharaTimerStart2580(uint kharaId) => KharaPackets.BuildAnsKharaTimerStart2580(kharaId);
    public static byte[] BuildPointChange2502(uint balance) => KharaPackets.BuildPointChange2502(balance);
    public static byte[] BuildNfyDeleteAcceptedKhara2858(IReadOnlyList<uint> kharaIds) => KharaPackets.BuildNfyDeleteAcceptedKhara2858(kharaIds);

    
 /// <summary>Loads the character's Khara state into the cache used by the login writer. Called at login.</summary>
    public static async Task PreloadAsync(uint characterId, IServiceProvider services, CancellationToken ct = default)
        => await GetPublishedAsync(characterId, Repository(services), ct);

 /// <summary>The cached Khara point balance of the character, or 0 when it has no state.</summary>
    public static uint PublishedPoints(uint characterId) =>
        Published.TryGetValue(characterId, out var doc) ? doc.Points : 0u;

 /// <summary>The ids of the Kharas the character has accepted and not completed, in ascending order.</summary>
    public static IReadOnlyList<uint> PublishedActiveIds(uint characterId) =>
        Published.TryGetValue(characterId, out var doc)
            ? doc.Active.Select(a => a.KharaId).Order().ToArray()
            : Array.Empty<uint>();

 /// <summary>
 /// Writes the accepted-Khara and completed-Khara lists of the 2310 login message from the cached state. Both are empty for a
 /// character without state.
 /// </summary>
    public static void WriteLoginVectors(PacketWriter writer, uint characterId, GameDataStore? data)
        => WriteLoginVectors(writer, Published.TryGetValue(characterId, out var doc) ? doc : null,
            data is null ? null : KharaCatalog.For(data));

 /// <summary>
 /// Writes the accepted-Khara and completed-Khara lists of the 2310 login message from a state document.
 /// </summary>
    public static void WriteLoginVectors(PacketWriter writer, KharaStateDocument? state, KharaCatalog? catalog)
    {
        var active = state?.Active ?? new List<KharaActiveDocument>();
        writer.WriteUInt16((ushort)active.Count);
        foreach (var a in active)
        {
            writer.WriteUInt32(a.KharaId);
            writer.WriteUInt32(a.State);
            writer.WriteUInt32(0);
            int rows = catalog?.Get(a.KharaId)?.Conditions.Count ?? a.Counts.Count;
            writer.WriteUInt16((ushort)rows);
            for (int i = 0; i < rows; i++)
                writer.WriteUInt32(i < a.Counts.Count ? a.Counts[i] : 0u);
        }
        var completed = state?.Completed ?? new List<uint>();
        writer.WriteUInt16((ushort)completed.Count);
        foreach (var id in completed)
            writer.WriteUInt32(id);
    }

    
 /// <summary>Handles a 1433 accept request: records the Khara and applies the accept rules.</summary>
    public static Task OnAcceptedAsync(ProudSession session, PlayerState player, uint kharaId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "accept",
            (state, catalog) => catalog.Get(kharaId) is not null && state.FindActive(kharaId) is null && !state.IsCompleted(kharaId),
            engine => engine.Accept(kharaId), ct);

 /// <summary>Handles a kill credited to the player.</summary>
    public static Task OnNpcKilledAsync(ProudSession session, PlayerState player, uint npcId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "kill",
            (state, catalog) => AnyActive(state, catalog, CompleteKillNpc, npcId)
                || KharaEngine.AnyAutoCandidate(catalog, state, CompleteKillNpc, npcId, CheapFactsOf(player)),
            engine => engine.OnNpcKilled(npcId), ct);

 /// <summary>
 /// Handles a change in the player's held count of <paramref name="itemId"/>. Call it after the inventory write and before the
 /// item's own message.
 /// </summary>
    public static Task OnHeldItemChangedAsync(ProudSession session, PlayerState player, uint itemId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "held-item",
            (state, catalog) => AnyActive(state, catalog, CompleteHoldItem, itemId)
                || KharaEngine.AnyAutoCandidate(catalog, state, CompleteHoldItem, itemId, CheapFactsOf(player)),
            engine => engine.OnHeldCountChanged(itemId), ct);

 /// <summary>Handles a quest the player completed. Call it at the end of quest reward payment.</summary>
    public static Task OnQuestCompletedAsync(ProudSession session, PlayerState player, uint questId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "quest",
            (state, catalog) => catalog.ReferencingQuest(questId).Any(d =>
                    state.FindActive(d.Id) is not null || KharaEngine.AutoEligible(state, d, CheapFactsOf(player)))
                
                
                || catalog.GatedByQuest(questId).Any(d => state.FindActive(d.Id) is null && !state.IsCompleted(d.Id)),
            engine => engine.OnQuestCompleted(questId), ct);

 /// <summary>Handles an item the player used up. Call it after the item is consumed and before its use notification.</summary>
    public static Task OnItemUsedAsync(ProudSession session, PlayerState player, uint itemId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteUseItem, itemId, "item-use", services, log, ct);

 /// <summary>Handles the player's death. Call it after the death notifications.</summary>
    public static Task OnDeathAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteDeath, 0, "death", services, log, ct);

 /// <summary>Handles the player's revival.</summary>
    public static Task OnRevivedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteRevived, 0, "revival", services, log, ct);

 /// <summary>Handles a harvest by the player.</summary>
    public static Task OnHarvestAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteHarvest, 0, "harvest", services, log, ct);

 /// <summary>Handles a skill that the player landed. Call it after the skill's own messages.</summary>
    public static Task OnSkillUsedAsync(ProudSession session, PlayerState player, uint skillId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteUseSkill, skillId / 100, "skill-use", services, log, ct);

 /// <summary>Handles the player summoning its Guardian.</summary>
    public static Task OnGuardianSummonedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteGuardianSummon, 0, "guardian", services, log, ct);

 /// <summary>Handles the player joining a guild.</summary>
    public static Task OnGuildJoinedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "guild-join",
            (state, catalog) => AnyActive(state, catalog, CompleteInGuild, 0),
            engine => engine.OnGuildJoined(), ct);

 /// <summary>
 /// Handles a base level or profession level change. Call it at the end of the level-up messages, after the player's level is updated.
 /// </summary>
    public static Task OnLevelChangedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "level",
            (state, catalog) => AnyActive(state, catalog, CompleteBaseLevel, 0) || AnyActive(state, catalog, CompleteProfessionLevel, 0),
            engine => engine.OnLevelChanged(), ct);

 /// <summary>Handles an NPC repair of <paramref name="itemCount"/> items. Call it after the repair's result message.</summary>
    public static Task OnRepairedAsync(ProudSession session, PlayerState player, int itemCount,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => itemCount <= 0
            ? Task.CompletedTask
            : OnCountedAsync(session, player, CompleteRepairCount, 0, (uint)itemCount, "repair", services, log, ct);

    private static Task OnCountedAsync(ProudSession session, PlayerState player, byte completeType, uint subject,
        string trigger, IServiceProvider services, ILogger? log, CancellationToken ct)
        => OnCountedAsync(session, player, completeType, subject, 1, trigger, services, log, ct);

    private static Task OnCountedAsync(ProudSession session, PlayerState player, byte completeType, uint subject,
        uint amount, string trigger, IServiceProvider services, ILogger? log, CancellationToken ct)
        => RunAsync(session, player, services, log, trigger,
            (state, catalog) => AnyActive(state, catalog, completeType, subject)
                || KharaEngine.AnyAutoCandidate(catalog, state, completeType, subject, CheapFactsOf(player)),
            engine => engine.OnCountedEvent(completeType, subject, amount), ct);

 /// <summary>Handles a 1437 completion request: completes the Khara when it is accepted and every condition is met.</summary>
    public static Task RequestCompletionAsync(ProudSession session, PlayerState player, uint kharaId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "complete-request",
            (state, _) => state.FindActive(kharaId) is not null,
            engine =>
            {
                if (!engine.TryComplete(kharaId))
                    log?.LogInformation("{Name}: 1437 for Khara {Id} refused, a condition is not met", player.Name, kharaId);
            }, ct);

 /// <summary>
 /// Removes the accepted Kharas bound to another profession after a profession change, and returns the removed ids in ascending order.
 /// </summary>
 /// <remarks>
 /// A Khara is profession-bound when its main row names a profession. Removal is applied to the accepted list only; completed
 /// Kharas are kept. The removed ids are sent in the 2858 message.
 /// </remarks>
    public static async Task<IReadOnlyList<uint>> DropProfessionKharasAsync(PlayerState player, byte newProfession,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
    {
        try
        {
            var catalog = KharaCatalog.For(services.GetRequiredService<GameDataStore>());
            var repo = Repository(services);
            if (OtherProfessionKharas(await GetPublishedAsync(player.CharacterId, repo, ct), catalog, newProfession).Count == 0)
                return Array.Empty<uint>();

            var gate = Gates.GetOrAdd(player.CharacterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var current = await GetPublishedAsync(player.CharacterId, repo, ct);
                var removed = OtherProfessionKharas(current, catalog, newProfession);
                if (removed.Count == 0) return removed;

                var next = current.Clone();
                next.Active.RemoveAll(a => removed.Contains(a.KharaId));
                next.UpdatedUtc = DateTime.UtcNow;
                try { await repo.SaveAsync(next, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.LogError(ex, "Khara state of character {Char} not saved (profession change)", player.CharacterId);
                }
                Published[player.CharacterId] = next;
                log?.LogInformation("{Name}: profession change to {Profession} drops accepted Khara(s) {Ids}",
                    player.Name, newProfession, string.Join(",", removed));
                return removed;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Khara profession change for character {Char} failed; nothing removed", player.CharacterId);
            return Array.Empty<uint>();
        }
    }

    internal static List<uint> OtherProfessionKharas(KharaStateDocument state, KharaCatalog catalog, byte profession)
        => KharaRules.OtherProfessionKharas(state, catalog, profession);

    
    private static bool AnyActive(KharaStateDocument state, KharaCatalog catalog, byte completeType, uint subject)
    {
        foreach (var a in state.Active)
            if (catalog.Get(a.KharaId) is { } d && d.Conditions.Any(c => c.Complete_Type == completeType && RowSubject(c) == subject))
                return true;
        return false;
    }

 /// <summary>The character's profession for the auto-accept check, or 0 when there is no character snapshot.</summary>
    private static byte ProfessionOf(PlayerState player) =>
        player.Character is { } c ? CharacterHeadRules.CreationProfessionByte(c) : (byte)0;

 /// <summary>The character's combat class for the class-mask check, or 0 when there is no character snapshot.</summary>
    private static byte CombatClassOf(PlayerState player) =>
        player.Character is { } c ? CharacterHeadRules.CombatJobByte(c) : (byte)0;

 /// <summary>Character facts for the auto-accept check that need no database reads.</summary>
    private static KharaFacts CheapFactsOf(PlayerState player) => new()
    {
        Level = player.Level,
        Profession = ProfessionOf(player),
        CombatClass = CombatClassOf(player),
    };

    private static IKharaStateRepository Repository(IServiceProvider services) =>
        services.GetRequiredService<IKharaStateRepository>();

    private static async Task<KharaStateDocument> GetPublishedAsync(uint characterId, IKharaStateRepository repo, CancellationToken ct)
    {
        if (Published.TryGetValue(characterId, out var cached)) return cached;
        var loaded = await repo.GetAsync(characterId, ct) ?? new KharaStateDocument { Id = characterId };
        Normalize(loaded, characterId);
        return Published.GetOrAdd(characterId, loaded);
    }

 /// <summary>Restores the sorted, disjoint lists that the engine relies on in a loaded document.</summary>
    private static void Normalize(KharaStateDocument doc, uint characterId)
    {
        doc.Id = characterId;
        doc.Completed = doc.Completed.Distinct().OrderBy(x => x).ToList();
        doc.Active = doc.Active
            .Where(a => doc.Completed.BinarySearch(a.KharaId) < 0)
            .GroupBy(a => a.KharaId).Select(g => g.First())
            .OrderBy(a => a.KharaId).ToList();
    }

    private static async Task<KharaFacts> LoadFactsAsync(PlayerState player, IServiceProvider services)
    {
        var held = new Dictionary<uint, int>();
        try
        {
            var items = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
            foreach (var it in items)
            {
                
                
                
                if (it.StorageType > 1) continue;
                held[it.ItemId] = held.GetValueOrDefault(it.ItemId) + Math.Max(0, it.Count);
            }
        }
        catch {   }

        HashSet<uint> quests;
        try
        {
            quests = new HashSet<uint>(await services.GetRequiredService<IQuestCompletionView>()
                .GetCompletedQuestIdsAsync(player.CharacterId));
        }
        catch { quests = new HashSet<uint>(); }

        bool inGuild;
        try
        {
            await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildRepository>());
            inGuild = GuildService.Instance.GetByCharId(player.CharacterId) is not null;
        }
        catch { inGuild = false; }

        return new KharaFacts
        {
            Level = player.Level,
            HeldCount = id => held.GetValueOrDefault(id),
            IsQuestCompleted = quests.Contains,
            InGuild = inGuild,
            Profession = ProfessionOf(player),
            CombatClass = CombatClassOf(player),
            ProfessionLevel = player.Character?.JobLevel ?? 0,
        };
    }

    private static async Task RunAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger? log,
        string trigger, Func<KharaStateDocument, KharaCatalog, bool> relevant, Action<KharaEngine> apply, CancellationToken ct)
    {
        try
        {
            var catalog = KharaCatalog.For(services.GetRequiredService<GameDataStore>());
            var repo = Repository(services);
            if (!relevant(await GetPublishedAsync(player.CharacterId, repo, ct), catalog)) return;

            var gate = Gates.GetOrAdd(player.CharacterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var current = await GetPublishedAsync(player.CharacterId, repo, ct);
                if (!relevant(current, catalog)) return;

                var engine = new KharaEngine(catalog, current.Clone(), await LoadFactsAsync(player, services));
                apply(engine);
                if (engine.Changed)
                {
                    engine.State.UpdatedUtc = DateTime.UtcNow;
                    try { await repo.SaveAsync(engine.State, ct); }
                    catch (Exception ex)
                    {
                        
                        
                        log?.LogError(ex, "Khara state of character {Char} not saved ({Trigger})", player.CharacterId, trigger);
                    }
                    Published[player.CharacterId] = engine.State;
                }
                foreach (var f in engine.Frames)
                    await session.SendRmiAsync(f.Opcode, f.Body, ct);
                if (engine.Frames.Count > 0)
                    log?.LogDebug("{Name}: Khara {Trigger} sent {N} message(s)", player.Name, trigger, engine.Frames.Count);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Khara {Trigger} for character {Char} failed", trigger, player.CharacterId);
        }
    }
}
