using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Handles the death of a monster: credits the killer and its party (quest progress, Khara progress and experience), then
/// leaves the corpse with its drops.
/// </summary>
/// <remarks>
/// Each kill path calls <c>ProcessKillCreditAsync</c>, then sends the kill grant, then calls <c>ProcessNpcCorpseAsync</c>.
/// Experience grants use <c>CombatService</c>, injected through DI.
/// </remarks>
public class LootService
{
    private readonly GameDataStore _data;
    private readonly BroadcastService _broadcast;
    private readonly CombatService _combat;
    private readonly NpcStateManager _npcState;
    private readonly PartyService _party;
    private readonly PlayerStateManager _players;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LootService> _logger;

    public LootService(
        GameDataStore data,
        BroadcastService broadcast,
        CombatService combat,
        NpcStateManager npcState,
        PartyService party,
        PlayerStateManager players,
        IServiceScopeFactory scopeFactory,
        ILogger<LootService> logger)
    {
        _data = data;
        _broadcast = broadcast;
        _combat = combat;
        _npcState = npcState;
        _party = party;
        _players = players;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

 /// <summary>
 /// Processes the drops of a killed NPC: gold goes to the killer, and each item goes to the corpse for its looters, or
 /// directly into the killer's bag when corpse looting is disabled.
 /// </summary>
 /// <param name="sharedLooters">Party members who share the corpse with the killer.</param>
    public async Task ProcessKillDropsAsync(ProudSession killerSession, PlayerState killer, uint npcId, IServiceProvider? services = null, uint npcEntityId = 0,
        IReadOnlyList<GroundItemService.CorpseLooter>? sharedLooters = null)
    {
        
        
        
        var party = _party.GetPlayerParty(killer.SessionId);

        
        var npcInfo = _data.Npcs.GetValueOrDefault(npcId);
        if (npcInfo == null) return;

        var dropId = npcInfo.Drop_ID;
        if (dropId == 0) return;

        
        var drops = _data.RollDrops(dropId);
        if (drops.Count == 0) return;

        
        
        
        
        
        
        
        
        
        var deathStamp = npcEntityId != 0 ? _npcState.GetNpc(npcEntityId)?.DeathTime : null;
        if (GroundItemService.CorpseLootEnabled && deathStamp != null)
        {
            GroundItemService.BeginCorpse(killer.MapId, npcEntityId, npcId, deathStamp, killer.CharacterId,
                ReceiverGid(killer), unlootedLifetime: UnlootedCorpseLifetime(_data.ConfigDef, npcInfo, _logger),
                sharedLooters: sharedLooters);
            foreach (var (itemId, count) in drops)
            {
                if (itemId == 0)
                {
                    
                    var zenyBody = ActorVitalsPackets.ApplyZenyDeltaAndBuild(killer, count);
                    await killerSession.SendRmiAsync(ZoneS2COpcodes.OnZenyChange, writer => writer.WriteBytes(zenyBody));
                    _logger.LogDebug("{Name} looted {Gold} gold", killer.Name, count);
                    continue;
                }

                
                var diceBound = PartyDiceRolls.SendsToDice(party, _data.GetItem(itemId));
                GroundItemService.AddCorpseItem(killer.MapId, npcEntityId, itemId, count, diceBound);
                _logger.LogDebug("{Name}: {Count}x item {Id} left on corpse {Corpse:X}{Dice}",
                    killer.Name, count, itemId, npcEntityId, diceBound ? " for the party roll" : "");
            }
            return;
        }

        
        
        
        
        
        
        IInventoryRepository? inventory = null;
        LootLanding?[]? landings = null;
        if (services != null)
        {
            try
            {
                inventory = services.GetRequiredService<IInventoryRepository>();
                var owned = await inventory.GetByCharacterAsync(killer.CharacterId);
                landings = PlanAutoLoot(drops, owned, killer.CharacterId);
            }
            catch {   }
        }

        
        
        
        
        
        
        var itemsOnCorpse = landings != null
            ? landings.Count(l => l is { Refused: false })
            : drops.Count(d => d.ItemId != 0);

        for (int k = 0; k < drops.Count; k++)
        {
            var (itemId, count) = drops[k];
            if (itemId == 0)
            {
                
                var zenyBody = ActorVitalsPackets.ApplyZenyDeltaAndBuild(killer, count);
                await killerSession.SendRmiAsync(ZoneS2COpcodes.OnZenyChange,
                    writer => writer.WriteBytes(zenyBody));
                _logger.LogDebug("{Name} looted {Gold} gold", killer.Name, count);
            }
            else
            {
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                var landing = landings?[k];
                if (landings != null && landing is null)
                    continue; 
                if (landing is { Refused: true })
                {
                    var refusal = LootNotifyPackets.BuildPickupItem2333(0UL, itemId, 0,
                        resultCode: CommonErrorPackets.NoFreeBagCell, pickupKind: 4);
                    await killerSession.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                        writer => writer.WriteBytes(refusal));
                    await killerSession.SendRmiAsync(ZoneS2COpcodes.OnCommonError,
                        CommonErrorPackets.BuildCommonError2438(CommonErrorPackets.NoFreeBagCell, CommonErrorPackets.ModeLoot));
                    _logger.LogInformation("{Name}: bag full, {Count}x item {Id} not looted", killer.Name, count, itemId);
                    continue;
                }

                
                
                
                landing ??= new LootLanding(new[] { NewLootStack(killer.CharacterId, itemId, count, -1, TemplateDurability(itemId)) });
                var landed = new List<InventoryItem>(1);
                if (landing.MergeInto is { } held)
                {
                    held.Count = landing.MergedCount;
                    try { if (inventory != null) await inventory.UpdateItemAsync(held); }
                    catch {   }
                    landed.Add(held);
                }
                foreach (var stack in landing.NewStacks)
                {
                    if (services != null)
                    {
                        try
                        {
                            inventory ??= services.GetRequiredService<IInventoryRepository>();
                            await inventory.AddItemAsync(stack); 
                        }
                        catch {   }
                    }
                    landed.Add(stack);
                }

                
                
                
                await AdvanceCollectQuestsAsync(killerSession, killer, itemId, count);

                
                
                
                
                
                if (services != null)
                    await KharaService.OnHeldItemChangedAsync(killerSession, killer, itemId, services, _logger);

                
                
                
                
                
                
                
                
                
                
                
                
                
                foreach (var stack in landed)
                {
                    var pickupBody = LootNotifyPackets.BuildPickupItem2333(
                        ItemPackets.BuildItemRecord126(stack));
                    await killerSession.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                        writer => writer.WriteBytes(pickupBody));
                }

                
                
                
                
                
                if (services != null)
                    await RankingService.CountCardLandingAsync(services, killer, itemId, count, landed, _logger);

                
                itemsOnCorpse--;

                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                if (party != null)
                {
                    var notifyBody = PartyLootNotifyPackets.BuildPartyNotifyGetItem2362(
                        ReceiverGid(killer), landed[0].Id, npcEntityId, itemId,
                        trailing: (byte)Math.Clamp(itemsOnCorpse, 0, byte.MaxValue));
                    await _party.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyGetItem, notifyBody);
                }

                var item = _data.GetItem(itemId);
                _logger.LogDebug("{Name} looted {Count}x {Item} (ID: {Id})",
                    killer.Name, count, item?.Name ?? "Unknown", itemId);
            }
        }
    }

 /// <summary>Where one rolled drop goes: merged into an existing stack, opened as new stacks, or refused for lack of space.</summary>
    private sealed class LootLanding
    {
        public LootLanding(IReadOnlyList<InventoryItem> newStacks) => NewStacks = newStacks;
        public InventoryItem? MergeInto { get; init; }
        public int MergedCount { get; init; }
        public IReadOnlyList<InventoryItem> NewStacks { get; }
        public bool Refused { get; init; }
    }

 /// <summary>
 /// Plans the automatic looting of <paramref name="drops"/> into the bag without writing anything. Returns one entry per drop,
 /// and null for gold. A stackable item joins an existing stack when the whole quantity fits within the item's stack maximum.
 /// Otherwise it opens new stacks in the lowest free cells, and is refused when there are not enough cells.
 /// </summary>
 /// <remarks>
 /// A stack created by one drop also takes later drops from the same kill. A new stack takes the template's durability.
 /// </remarks>
    private LootLanding?[] PlanAutoLoot(IReadOnlyList<(uint ItemId, int Count)> drops,
        IReadOnlyList<InventoryItem> owned, uint characterId)
    {
        var free = BagCapacity.FreeCells(owned, _data.Items);
        var bagRows = owned.Where(i => i.StorageType == 0 && i.Slot >= 0 && i.Count > 0).ToList();
        var entries = new List<(uint, int, byte)>(drops.Count);
        foreach (var (itemId, count) in drops)
            entries.Add((itemId, count, _data.GetItem(itemId)?.Stack_Max ?? (byte)1));

        // The placement rule is the shared Inventory.Domain primitive; this maps its result onto LootLanding and
        // keeps the per-drop refusal (a full bag refuses only that drop; later drops may still land).
        var seq = BagStackPlanner.PlanSequence(
            bagRows.Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList(), free, entries);

        var landings = new LootLanding?[drops.Count];
        var bySlot = new Dictionary<short, InventoryItem>();
        foreach (var i in bagRows) bySlot[i.Slot] = i;
        for (int k = 0; k < drops.Count; k++)
        {
            var (itemId, count) = drops[k];
            var pl = seq[k];
            if (pl is null) continue;
            if (pl.Refused)
            {
                landings[k] = new LootLanding(Array.Empty<InventoryItem>()) { Refused = true };
                continue;
            }
            if (pl.MergeIntoSlot is { } slot)
            {
                landings[k] = new LootLanding(Array.Empty<InventoryItem>())
                {
                    MergeInto = bySlot[slot],
                    MergedCount = pl.MergedCount,
                };
                continue;
            }
            short durability = TemplateDurability(itemId);
            var opened = new List<InventoryItem>(pl.Opened.Count);
            foreach (var (cell, n) in pl.Opened)
            {
                var stack = NewLootStack(characterId, itemId, n, cell, durability);
                opened.Add(stack);
                bySlot[cell] = stack;
            }
            landings[k] = new LootLanding(opened);
        }
        return landings;
    }

 /// <summary>
 /// Creates a bag row for looted items in <paramref name="slot"/>, or with an automatically chosen slot when -1.
 /// </summary>
    private static InventoryItem NewLootStack(uint characterId, uint itemId, int count, short slot, short durability)
        => new()
        {
            CharacterId = characterId,
            ItemId = itemId,
            Count = count,
            Slot = slot,
            StorageType = 0, 
            Durability = durability,
        };

 /// <summary>Durability of a new looted row: the item template's durability, or -1 when the template is unknown.</summary>
    private short TemplateDurability(uint itemId)
        => _data.GetItem(itemId) is { } t ? (short)Math.Min(t.Durability, (uint)short.MaxValue) : (short)-1;

 /// <summary>
 /// Applies the credit for a kill, in order: quest progress ticks, Khara progress, then the experience grant with its
 /// level-up notifications and each receiver's compensation. The corpse is not handled here; see <see cref="ProcessNpcCorpseAsync"/>.
 /// </summary>
    public async Task ProcessKillCreditAsync(
        ProudSession killerSession, PlayerState killer, uint npcEntityId, uint npcId)
    {
        var expReward = KillExpReward(npcEntityId, npcId);
        await AdvanceKillQuestsAsync(killerSession, killer, npcId);
        
        
        
        await QuestFailTriggers.OnNpcKilledAsync(killerSession, killer, npcId, _logger);
        await CreditKharaKillAsync(killerSession, killer, npcId);
        await GrantKillExpAsync(killer, expReward);
        if (expReward > 0)
            await CountRankingKillAsync(killer);
        _logger.LogDebug("{Name} credited for NPC {NpcId} (entity: {Entity}) - EXP: {Exp}",
            killer.Name, npcId, npcEntityId, expReward);
    }

 /// <summary>
 /// Counts one kill toward the killer's daily hunter ranking. Only kills that grant experience are counted, and only the killer is counted.
 /// </summary>
    private async Task CountRankingKillAsync(PlayerState killer)
    {
        using var scope = _scopeFactory.CreateScope();
        await RankingService.CountAsync(scope.ServiceProvider, killer, RankingSchedule.TypeMonsterHunt, _logger);
    }

 /// <summary>
 /// Handles the corpse of a kill: notifies the viewers, rewards dungeon boss kills, leaves the drops on the corpse or
 /// auto-loots them, and schedules the corpse's removal.
 /// </summary>
 /// <remarks>
 /// In a shared loot party, every member on the killer's map gets their own corpse grant before the corpse is announced.
 /// </remarks>
    public async Task ProcessNpcCorpseAsync(
        ProudSession killerSession, PlayerState killer, uint npcEntityId, uint npcId)
    {
        var sharers = SharedCorpseLooters(killer, npcEntityId);
        foreach (var member in sharers)
            if (_broadcast.GetSession(member.SessionId) is { } memberSession)
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                    CombatPackets.BuildLootingAuthority2471(npcEntityId));

        var corpse = await AnnounceDeathAsync(killerSession, killer, npcEntityId, npcId, sharers);
        await LeaveCorpseAsync(killerSession, killer, npcEntityId, npcId, corpse, sharers);
    }

 /// <summary>
 /// Party members besides the killer who may loot this kill's corpse. Empty for a solo killer, for a party whose loot mode
 /// does not share corpses, or when corpse looting does not apply to the kill.
 /// </summary>
    internal IReadOnlyList<PlayerState> SharedCorpseLooters(PlayerState killer, uint npcEntityId)
    {
        if (!GroundItemService.CorpseLootEnabled || _npcState.GetNpc(npcEntityId)?.DeathTime is null)
            return Array.Empty<PlayerState>();
        var party = _party.GetPlayerParty(killer.SessionId);
        if (party is null || !GroundItemService.SharesCorpseLoot(party.ItemRule))
            return Array.Empty<PlayerState>();

        var members = new List<PlayerState>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (_party.GetOnlineMember(party, memberId) is not { } m) continue;
            if (m.SessionId == killer.SessionId || m.CharacterId == killer.CharacterId) continue;
            if (m.MapId == killer.MapId)
                members.Add(m);
        }
        return members;
    }

 /// <summary>Experience of the kill: the NPC's runtime value, else the template's reward, else 10.</summary>
    private ulong KillExpReward(uint npcEntityId, uint npcId)
        => (ulong)(_npcState.GetNpc(npcEntityId)?.ExpReward ?? _data.GetNpcStats(npcId)?.EXP_Reward ?? 10);

 /// <summary>Advances the killer's Khara kill progress and reports it to the client.</summary>
    private async Task CreditKharaKillAsync(ProudSession killerSession, PlayerState killer, uint npcId)
    {
        using var kharaScope = _scopeFactory.CreateScope();
        await KharaService.OnNpcKilledAsync(killerSession, killer, npcId, kharaScope.ServiceProvider, _logger);
    }

 /// <summary>True when the NPC's corpse can be gathered.</summary>
    private bool IsGatherable(uint npcId) => (_data.Npcs.GetValueOrDefault(npcId)?.AfterDeath_FuncObject_ID ?? 0) != 0;

 /// <summary>
 /// Announces a death to the viewers and the dungeon finder boss reward. Returns the map of the corpse and the time of death.
 /// </summary>
    private async Task<(uint MapId, DateTime? DeathStamp)> AnnounceDeathAsync(ProudSession killerSession,
        PlayerState killer, uint npcEntityId, uint npcId, IReadOnlyList<PlayerState> sharers)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        if (IsGatherable(npcId))
        {
            var gatherBody = CorpseGatherPackets.BuildNotifyCanGatherDeadNpc2534(npcEntityId);
            foreach (var viewer in _players.GetPlayersOnMap(killer.MapId).ToList())
            {
                if (viewer.SessionId == killer.SessionId || sharers.Any(m => m.SessionId == viewer.SessionId))
                    continue;
                if (_broadcast.GetSession(viewer.SessionId) is { } viewerSession)
                    await viewerSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc, gatherBody);
            }
        }

        
        
        
        var deadBody = ActorVitalsPackets.BuildDeadNotify2392(npcEntityId, killer.EntityId);
        await _broadcast.BroadcastToMapAsync(killer.MapId, ZoneS2COpcodes.OnDeadNotify,
            writer => writer.WriteBytes(deadBody));

        
        
        if (DungeonFinderPendingStore.GetRun(killer) is not null)
        {
            using var finderScope = _scopeFactory.CreateScope();
            await DungeonFinderReward.TryPayAsync(killerSession, killer, npcId, _data, this, finderScope.ServiceProvider, _logger);
        }

        
        
        return (killer.MapId, _npcState.GetNpc(npcEntityId)?.DeathTime);
    }

 /// <summary>
 /// Leaves the death's drops on the corpse, or in the killer's bag when auto-looting. Revokes the killer's corpse grant and
 /// notifies viewers when nothing is left.
 /// </summary>
    private async Task LeaveCorpseAsync(ProudSession killerSession, PlayerState killer, uint npcEntityId, uint npcId,
        (uint MapId, DateTime? DeathStamp) corpse, IReadOnlyList<PlayerState> sharers)
    {
        var (corpseMapId, deathStamp) = corpse;

        
        
        
        
        
        var sharedLooters = sharers.Count == 0
            ? null
            : sharers.Select(m => new GroundItemService.CorpseLooter(m.CharacterId, ReceiverGid(m))).ToArray();
        using (var scope = _scopeFactory.CreateScope())
            await ProcessKillDropsAsync(killerSession, killer, npcId, scope.ServiceProvider, npcEntityId, sharedLooters);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (!GroundItemService.HasCorpseLoot(corpseMapId, npcEntityId, deathStamp))
        {
            var revoke = CombatPackets.BuildLootingAuthority2471(npcEntityId, granted: false);
            var gatherable = IsGatherable(npcId);
            var gather = CorpseGatherPackets.BuildNotifyCanGatherDeadNpc2534(npcEntityId);
            await killerSession.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority, revoke);

            
            
            
            
            if (gatherable)
                await killerSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc, gather);

            foreach (var member in sharers)
            {
                if (_broadcast.GetSession(member.SessionId) is not { } memberSession) continue;
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority, revoke);
                if (gatherable)
                    await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc, gather);
            }
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var corpseSeconds = _data.Npcs.GetValueOrDefault(npcId)?.Corpse_Disappear_Time ?? 0f;
        var corpseStay = TimeSpan.FromSeconds(Math.Max(0f, corpseSeconds));
        
        if (IsGatherable(npcId))
            corpseStay += CorpseGatherWait();
        
        var audience = CorpseAudience.Capture(_players.GetPlayersOnMap(corpseMapId).Select(p => p.SessionId),
            killer.SessionId, sharers.Select(m => m.SessionId));
        
        
        var pending = deathStamp is { } stamp ? TrackPendingCorpse(npcEntityId, stamp) : null;
        if (audience.Bystanders.Count > 0)
            _ = DespawnCorpseForBystandersAsync(corpseMapId, npcEntityId, deathStamp,
                CorpseAudience.BystanderDelay(deathStamp, corpseStay, DateTime.UtcNow), audience);
        _ = DespawnCorpseAsync(corpseMapId, npcEntityId, deathStamp, corpseStay, pending, audience);
    }

 /// <summary>
 /// Sends the corpse removal to the bystanders, after the corpse's stay counted from the death. Skipped when the handle names
 /// a live NPC or a later death. It does not release the corpse; <see cref="DespawnCorpseAsync"/> does.
 /// </summary>
    private async Task DespawnCorpseForBystandersAsync(uint mapId, uint npcEntityId, DateTime? deathStamp, TimeSpan delay,
        CorpseAudience audience)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            var npc = _npcState.GetNpc(npcEntityId);
            if (npc != null && (!npc.IsDead || npc.DeathTime != deathStamp))
                return;
            await SendCorpseOutAsync(mapId, npcEntityId, audience.Claim(audience.Bystanders));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bystander corpse despawn (2507) for NPC entity {Entity} on map {Map} failed",
                npcEntityId, mapId);
        }
    }

 /// <summary>Sends the corpse removal to each listed session whose player is still on the map.</summary>
    private async Task SendCorpseOutAsync(uint mapId, uint npcEntityId, IReadOnlyList<uint> sessionIds)
    {
        if (sessionIds.Count == 0) return;
        var outBody = NpcLifecyclePackets.BuildNpcOut2507(npcEntityId);
        foreach (var sessionId in sessionIds)
        {
            if (_players.GetPlayer(sessionId)?.MapId != mapId) continue;
            if (_broadcast.GetSession(sessionId) is { } viewer)
                await viewer.SendInternalRmiAsync(ZoneS2COpcodes.OnNPC_Out, outBody);
        }
    }

 /// <summary>
 /// Sends the corpse removal to the viewers who have not yet received it, once the corpse may leave. Runs detached from the
 /// kill, so the corpse does not hold up the attack handler.
 /// </summary>
    private async Task DespawnCorpseAsync(uint mapId, uint npcEntityId, DateTime? deathStamp, TimeSpan delay,
        PendingCorpse? pending, CorpseAudience audience)
    {
        try
        {
            await GroundItemService.WaitUntilCorpseMayLeaveAsync(mapId, npcEntityId, deathStamp, delay);

            var npc = _npcState.GetNpc(npcEntityId);
            if (npc != null && (!npc.IsDead || npc.DeathTime != deathStamp))
                return;

            await SendCorpseOutAsync(mapId, npcEntityId, audience.Claim(audience.All));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Corpse despawn (2507) for NPC entity {Entity} on map {Map} failed",
                npcEntityId, mapId);
        }
        finally
        {
            
            
            if (pending != null)
                ReleasePendingCorpse(npcEntityId, pending);
            _npcState.RetireCorpse(npcEntityId, deathStamp);
        }
    }

 /// <summary>
 /// The sessions that saw a corpse appear, split into looters and bystanders. Each viewer receives the corpse removal once.
 /// </summary>
    internal sealed class CorpseAudience
    {
        private readonly Lock _gate = new();
        private readonly HashSet<uint> _sent = new();

        private CorpseAudience(IReadOnlyList<uint> looters, IReadOnlyList<uint> bystanders)
        {
            Looters = looters;
            Bystanders = bystanders;
            All = looters.Concat(bystanders).ToArray();
        }

 /// <summary>Sessions with loot authority over the corpse.</summary>
        public IReadOnlyList<uint> Looters { get; }

 /// <summary>Sessions without loot authority over the corpse.</summary>
        public IReadOnlyList<uint> Bystanders { get; }

 /// <summary>All viewers: looters, then bystanders.</summary>
        public IReadOnlyList<uint> All { get; }

 /// <summary>
 /// Splits the sessions on the corpse's map into looters (the killer and the sharing party members) and bystanders.
 /// The killer is always a looter.
 /// </summary>
        internal static CorpseAudience Capture(IEnumerable<uint> onMap, uint killerSession, IEnumerable<uint> sharerSessions)
        {
            var authority = new HashSet<uint>(sharerSessions) { killerSession };
            var looters = new List<uint> { killerSession };
            var bystanders = new List<uint>();
            foreach (var id in onMap.Distinct())
            {
                if (id == killerSession) continue;
                if (authority.Contains(id)) looters.Add(id);
                else bystanders.Add(id);
            }
            return new CorpseAudience(looters, bystanders);
        }

 /// <summary>Returns the sessions that have not yet received the corpse removal, and marks them as having it.</summary>
        internal IReadOnlyList<uint> Claim(IEnumerable<uint> sessionIds)
        {
            var due = new List<uint>();
            lock (_gate)
                foreach (var id in sessionIds)
                    if (_sent.Add(id)) due.Add(id);
            return due;
        }

 /// <summary>Time from now until the bystanders' corpse removal: the stay counted from the death, and never negative.</summary>
        internal static TimeSpan BystanderDelay(DateTime? deathStamp, TimeSpan stay, DateTime nowUtc)
        {
            var left = deathStamp is { } died ? died + stay - nowUtc : stay;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

 /// <summary>A corpse whose removal has not been sent yet, identified by its death time.</summary>
    internal sealed class PendingCorpse(DateTime deathStamp)
    {
        public DateTime DeathStamp { get; } = deathStamp;
    }

 /// <summary>Corpses whose removal has not been sent yet, by NPC handle.</summary>
    private static readonly ConcurrentDictionary<uint, PendingCorpse> PendingCorpses = new();

 /// <summary>Marks the corpse of a death as present on the clients until it is released.</summary>
    internal static PendingCorpse TrackPendingCorpse(uint npcEntityId, DateTime deathStamp)
    {
        var pending = new PendingCorpse(deathStamp);
        PendingCorpses[npcEntityId] = pending;
        return pending;
    }

 /// <summary>Releases a pending corpse. Only that exact entry is removed, never a later death's entry under the same handle.</summary>
    internal static void ReleasePendingCorpse(uint npcEntityId, PendingCorpse pending)
        => PendingCorpses.TryRemove(new KeyValuePair<uint, PendingCorpse>(npcEntityId, pending));

 /// <summary>True while the corpse of the given death is still on the clients.</summary>
    internal static bool CorpseOnClients(uint npcEntityId, DateTime? deathStamp)
        => deathStamp is { } stamp && PendingCorpses.TryGetValue(npcEntityId, out var pending)
           && pending.DeathStamp == stamp;

 /// <summary>
 /// Advances the killer's active kill quests that target the killed monster, and sends the progress tick to the killer.
 /// </summary>
 /// <remarks>
 /// Each kill row of a quest is an objective with its own count. Progress is stored with the quest and restored at the next
 /// login. When every objective of a quest is met, the closing quest update is sent, and a reward-auto quest is paid at
 /// that point.
 /// </remarks>
    private async Task AdvanceKillQuestsAsync(ProudSession killerSession, PlayerState killer, uint npcId)
    {
        var extra = ExtraKillProgress.GetValue(killer, _ => new Dictionary<uint, ExtraObjectives>());
        var closedSet = ClosedKillObjectives.GetValue(killer, _ => new ConcurrentDictionary<uint, byte>());

        
        
        var active = killer.ActiveQuests.ToArray();
        lock (extra)
        {
            
            foreach (var gone in extra.Keys.Where(q => Array.IndexOf(active, q) < 0).ToList())
                extra.Remove(gone);
        }
        
        foreach (var gone in closedSet.Keys.Where(q => Array.IndexOf(active, q) < 0).ToList())
            closedSet.TryRemove(gone, out _);

        foreach (var questId in active)
        {
            if (!_data.QuestRows.TryGetValue(questId, out var rows)) continue;
            var ticked = false;

            ExtraObjectives? later = null;
            if (rows.Length > 1)
            {
                killer.QuestKillProgress.TryGetValue(questId, out var firstNow);
                lock (extra)
                {
                    if (!extra.TryGetValue(questId, out later))
                        extra[questId] = later = new ExtraObjectives();
                    
                    
                    else if (firstNow < later.FirstRowSeen)
                        later.Counts.Clear();
                }
            }

            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Complete_Type != 2 || row.Complete_Type_Value1 != npcId) continue;

                
                
                var needed = QuestProgressStore.NeededKills(row);
                ushort current;
                if (i == 0)
                {
                    killer.QuestKillProgress.TryGetValue(questId, out current);
                    if (current >= needed) continue; 
                    current = (ushort)(current + 1);
                    killer.QuestKillProgress[questId] = current;
                }
                else
                {
                    lock (extra)
                    {
                        later!.Counts.TryGetValue(npcId, out current);
                        if (current >= needed) continue;
                        current = (ushort)(current + 1);
                        later.Counts[npcId] = current;
                    }
                }

                await killerSession.SendRmiAsync(ZoneS2COpcodes.OnQuestMonsterOrGO,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestMonsterOrGo2425(questId, npcId, current)));
                ticked = true;

                _logger.LogInformation("{Name} quest {QuestId}: killed {Npc} ({Cur}/{Need}, objective row {Row})",
                    killer.Name, questId, npcId, current, needed, i);
            }

            if (later is not null)
            {
                killer.QuestKillProgress.TryGetValue(questId, out var firstAfter);
                lock (extra) later.FirstRowSeen = firstAfter;
            }

            if (!ticked) continue;

            
            
            var closes = AllKillObjectivesMet(killer, questId, rows) && closedSet.TryAdd(questId, 0);
            if (closes)
            {
                await killerSession.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify,
                    writer => writer.WriteBytes(QuestProgressPackets.BuildQuestComplete2427(questId)));
                _logger.LogInformation("{Name} quest {QuestId}: kill objective met", killer.Name, questId);
            }

            
            
            
            await SaveKillProgressAsync(killer, questId, rows);

            if (closes && QuestInfoExtraColumns.IsRewardAuto(questId))
                await PayRewardAutoKillQuestAsync(killerSession, killer, questId);
        }
    }

 /// <summary>Stores a quest's kill counts on its in-progress quest document. A failed write is logged and the kill continues.</summary>
    private async Task SaveKillProgressAsync(PlayerState player, uint questId, QuestData[] rows)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            
            
            if (scope.ServiceProvider.GetService<IQuestRepository>() is not { } quests) return;
            await QuestProgressStore.SaveKillProgressAsync(quests, player, questId, rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} quest {QuestId}: storing the kill progress failed", player.Name, questId);
        }
    }

 /// <summary>
 /// Restores a quest's stored kill counts into this login's counters at world entry. A quest whose objectives are all met
 /// by kills is marked as closed.
 /// </summary>
    internal static void RestoreKillProgress(PlayerState player, uint questId, QuestData[] rows, ReadOnlySpan<ushort> counts)
    {
        ExtraObjectives? later = null;
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < counts.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == 0) continue;
            var count = counts[objective++];
            if (!QuestProgressStore.IsKillRow(row) || count == 0) continue;
            count = Math.Min(count, QuestProgressStore.NeededKills(row));
            if (i == 0)
                player.QuestKillProgress[questId] = count;
            else
                (later ??= new ExtraObjectives()).Counts[row.Complete_Type_Value1] = count;
        }

        if (later is not null)
        {
            player.QuestKillProgress.TryGetValue(questId, out var firstRow);
            later.FirstRowSeen = firstRow;
            var extra = ExtraKillProgress.GetValue(player, _ => new Dictionary<uint, ExtraObjectives>());
            lock (extra) extra[questId] = later;
        }

        if (AllKillObjectivesMet(player, questId, rows))
            ClosedKillObjectives.GetValue(player, _ => new ConcurrentDictionary<uint, byte>()).TryAdd(questId, 0);
    }

 /// <summary>True when every objective row of the quest is a kill row whose count has reached its target.</summary>
    internal static bool AllKillObjectivesMet(PlayerState player, uint questId, QuestData[] rows)
    {
        var any = false;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == 0) continue;
            if (row.Complete_Type != 2 || row.Complete_Type_Value1 == 0) return false;
            var needed = QuestProgressStore.NeededKills(row);
            ushort have;
            if (i == 0) player.QuestKillProgress.TryGetValue(questId, out have);
            else have = ExtraKillCount(player, questId, row.Complete_Type_Value1);
            if (have < needed) return false;
            any = true;
        }
        return any;
    }

 /// <summary>
 /// Completes and pays a reward-auto kill quest at the kill that closed it. The quest is removed from the active set first,
 /// so it cannot be paid twice.
 /// </summary>
    private async Task PayRewardAutoKillQuestAsync(ProudSession session, PlayerState player, uint questId)
    {
        if (!_data.Quests.TryGetValue(questId, out var quest)) return;
        if (!player.ActiveQuests.Remove(questId)) return;
        player.QuestKillProgress.Remove(questId);
        ResetExtraKillProgress(player, questId);
        QuestUseItemProgress.Reset(player, questId);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IQuestRepository>().CompleteQuestAsync(player.CharacterId, questId);
            await QuestPrizeHandler.GrantRewardsAsync(
                session, player, quest, QuestPrizeHandler.NoRewardChoice, services, _logger);
            _logger.LogInformation("{Name} quest {QuestId}: paid at its last kill (Reward_Auto)", player.Name, questId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} quest {QuestId}: the Reward_Auto kill payout failed", player.Name, questId);
        }
    }

 /// <summary>
 /// Kill quests whose closing update was sent during this login, or that were already closed when the login began. Cleared
 /// together with the later-row kill counts.
 /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerState, ConcurrentDictionary<uint, byte>>
        ClosedKillObjectives = new();

 /// <summary>True when the kill quest's closing update was already sent during this login.</summary>
    internal static bool KillObjectiveClosed(PlayerState player, uint questId)
        => ClosedKillObjectives.TryGetValue(player, out var closed) && closed.ContainsKey(questId);

 /// <summary>Kill counts of one quest's objectives after its first row, keyed by target NPC id.</summary>
    private sealed class ExtraObjectives
    {
        public ushort FirstRowSeen;
        public readonly Dictionary<uint, ushort> Counts = new();
    }

 /// <summary>
 /// Per-login counts for kill objectives after a quest's first row. Restored from the quest's stored counts at world entry,
 /// and cleared when the quest is accepted, completed, turned in or abandoned.
 /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerState, Dictionary<uint, ExtraObjectives>>
        ExtraKillProgress = new();

 /// <summary>Kills counted toward a target on a kill row after the first row of the quest, or 0 when none.</summary>
    internal static ushort ExtraKillCount(PlayerState player, uint questId, uint targetId)
    {
        if (!ExtraKillProgress.TryGetValue(player, out var extra)) return 0;
        lock (extra)
            return extra.TryGetValue(questId, out var later) && later.Counts.TryGetValue(targetId, out var n) ? n : (ushort)0;
    }

 /// <summary>Forgets the later-row kill counts of a quest and its closure.</summary>
    internal static void ResetExtraKillProgress(PlayerState player, uint questId)
    {
        if (ClosedKillObjectives.TryGetValue(player, out var closed)) closed.TryRemove(questId, out _);
        if (!ExtraKillProgress.TryGetValue(player, out var extra)) return;
        lock (extra) extra.Remove(questId);
    }

 /// <summary>
 /// Advances the player's active collect quests for an item that has just entered the bag, sends the progress tick, and closes
 /// the objective once the count reaches its target. Called before the item's own pickup notification.
 /// </summary>
 /// <remarks>
 /// The running count is the number of the item held, counted from the quest accept and the looted quantities. Progress
 /// shares the quest progress entry with kill quests, which cannot collide because each quest has a single objective type.
 /// </remarks>
    public async Task AdvanceCollectQuestsAsync(ProudSession session, PlayerState player, uint itemId, int count)
    {
        
        foreach (var questId in player.ActiveQuests.ToArray())
        {
            if (!_data.Quests.TryGetValue(questId, out var quest)) continue;
            if (quest.Complete_Type != 4 || quest.Complete_Type_Value1 != itemId) continue;

            
            var needed = quest.Complete_Type_Value2 == 0 ? (byte)1 : (byte)Math.Min(quest.Complete_Type_Value2, 255);
            player.QuestKillProgress.TryGetValue(questId, out var current);
            if (current >= needed) continue;

            current = (ushort)Math.Min(current + Math.Max(count, 1), needed);
            player.QuestKillProgress[questId] = current;

            var tickBody = QuestNotifyPackets.BuildQuestAddItem2424(questId, itemId, current);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestAddItem, writer => writer.WriteBytes(tickBody));

            if (current >= needed)
            {
                var completeBody = QuestProgressPackets.BuildQuestComplete2427(questId);
                await session.SendRmiAsync(ZoneS2COpcodes.OnQuestCompleteNotify, writer => writer.WriteBytes(completeBody));

                
                
                
                
                if (QuestAcceptHandler.ObjectiveCloseGrants(questId).Count > 0)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await QuestPrizeHandler.GrantObjectiveCloseItemsAsync(session, player, questId, _data,
                            scope.ServiceProvider.GetRequiredService<IInventoryRepository>(), _logger);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Name} quest {QuestId}: the objective-close grant failed", player.Name, questId);
                    }
                }
            }

            _logger.LogInformation("{Name} quest {QuestId}: collected {Item} ({Cur}/{Need})",
                player.Name, questId, itemId, current, needed);
        }
    }

 /// <summary>
 /// Grants the kill experience. Without a party the killer receives the full amount. With a party the reward is split equally
 /// among the party members on the killer's map, with any remainder going to the killer, so no experience is created.
 /// </summary>
    private async Task GrantKillExpAsync(PlayerState killer, ulong expReward)
    {
        var party = _party.GetPlayerParty(killer.SessionId);
        if (party == null)
        {
            
            await GrantAndCompensateAsync(killer, expReward);
            return;
        }

        
        
        var members = new List<PlayerState>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            var m = _players.GetPlayer(memberId);
            if (m != null && m.MapId == killer.MapId && !m.IsDead)
                members.Add(m);
        }

        
        
        if (members.Count <= 1 || party.ExpRule == 1)
        {
            await GrantAndCompensateAsync(killer, expReward);
            return;
        }

        
        var per = expReward / (ulong)members.Count;
        var remainder = expReward - per * (ulong)members.Count;
        foreach (var m in members)
        {
            var amount = per + (m.SessionId == killer.SessionId ? remainder : 0UL);
            if (amount > 0)
                await GrantAndCompensateAsync(m, amount);
        }

        _logger.LogDebug("Party {PartyId}: shared {Exp} EXP among {Count} nearby members",
            party.PartyId, expReward, members.Count);
    }

 /// <summary>
 /// Grants one receiver's share of kill experience, then sends that receiver its compensation notification so the experience
 /// bar updates on every kill.
 /// </summary>
    private async Task GrantAndCompensateAsync(PlayerState player, ulong amount)
    {
        int levelBefore = player.Level;
        await _combat.GrantExpAsync(player, amount);
        await CompensationPackets.SendAfterKillGrantAsync(_broadcast.GetSession(player.SessionId), player, levelBefore);
    }

 /// <summary>Character id used for a party loot notification: the 64-bit character uid when set, else the 32-bit id.</summary>
    private static ulong ReceiverGid(PlayerState p) => p.CharacterUid != 0 ? p.CharacterUid : p.CharacterId;

 /// <summary>Config_Def type of the wait, in seconds, before an unlooted normal monster corpse disappears.</summary>
    private const ushort ConfigNormalMonsterLootWait = 22;

 /// <summary>Config_Def type of the extra wait, in seconds, for gatherable monster corpses.</summary>
    internal const ushort ConfigCorpseGatherWait = 30;

 /// <summary>Extra time a gatherable corpse stays after its removal time. Zero when the config row is missing or unreadable.</summary>
    private TimeSpan CorpseGatherWait() => CorpseGatherWait(_data, _logger);

 /// <summary>Reads the gatherable corpse wait from the config table. Internal so tests can read the shipped value.</summary>
    internal static TimeSpan CorpseGatherWait(GameDataStore data, ILogger logger)
    {
        var row = data.ConfigDef.FirstOrDefault(c => c.type == ConfigCorpseGatherWait);
        if (row != null && int.TryParse(row.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        logger.LogWarning("Config_Def type {Type} (corpse gathering wait) is missing or unreadable; gatherable corpses "
            + "leave at their Corpse_Disappear_Time", ConfigCorpseGatherWait);
        return TimeSpan.Zero;
    }

 /// <summary>Config_Def type of the wait, in seconds, for unlooted corpses of named and dungeon elite monsters.</summary>
    private const ushort ConfigNamedEliteLootWait = 23;

 /// <summary>True for the NPC grades that use the named and elite loot wait.</summary>
    private static bool IsNamedEliteGrade(int grade) => grade is 12 or 13;

 /// <summary>
 /// How long the corpse keeps unclaimed items, counted from the death. Named and dungeon elite monsters (grades 12 and 13)
 /// use the longer wait; other grades use the normal wait. Falls back to the normal wait when a config row is missing.
 /// </summary>
 /// <remarks>
 /// The respawn of the NPC does not end this lifetime, because the respawn gets a fresh handle.
 /// </remarks>
    internal static TimeSpan UnlootedCorpseLifetime(IReadOnlyList<RO2.GameData.Tables.ConfigDef> config,
        NpcData? npc, ILogger logger)
    {
        if (npc != null && IsNamedEliteGrade(npc.Grade))
        {
            if (ConfigSeconds(config, ConfigNamedEliteLootWait) is { } elite)
                return elite;
            logger.LogWarning("Config_Def type {Type} (named/elite looting wait) is missing or unreadable; the grade-{Grade} "
                + "corpse of NPC {Npc} keeps its items for type {Fallback}'s lifetime", ConfigNamedEliteLootWait, npc.Grade,
                npc.ID, ConfigNormalMonsterLootWait);
        }
        if (ConfigSeconds(config, ConfigNormalMonsterLootWait) is { } normal)
            return normal;
        logger.LogWarning("Config_Def type {Type} (normal monster looting wait) is missing or unreadable; corpses keep "
            + "their items for {Seconds} s", ConfigNormalMonsterLootWait, GroundItemService.UnlootedCorpseLifetime.TotalSeconds);
        return GroundItemService.UnlootedCorpseLifetime;
    }

 /// <summary>Positive whole seconds of a config row, or null when the row is missing or not a positive integer.</summary>
    private static TimeSpan? ConfigSeconds(IReadOnlyList<RO2.GameData.Tables.ConfigDef> config, ushort type)
    {
        var row = config.FirstOrDefault(c => c.type == type);
        return row != null && int.TryParse(row.Value, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
