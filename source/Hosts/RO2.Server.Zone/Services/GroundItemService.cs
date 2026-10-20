using System;
using System.Collections.Concurrent;
using System.Linq;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Loot that a kill leaves on the corpse. Looters list the corpse, take its items one at a time, or roll for them
/// as a party.
/// </summary>
/// <remarks>
/// <para>
/// Items stay on the corpse until a looter takes them. Gold goes straight to the purse. <c>LootService</c> fills the
/// corpse, the 1338 handler lists it, and the item pickup handler takes items from it. Party dice rolls go through
/// <c>PartyDiceService</c>.
/// </para>
/// <para>
/// Who may loot: the killer, and in a party with a shared loot mode (Roll Dice, Free for All or Wearer Roll Dice),
/// every member on the killer's map.
/// </para>
/// <para>
/// A corpse is keyed by its map and the dead NPC's handle. The handle keeps resolving to the corpse after the NPC
/// respawns under a fresh handle. A corpse that nobody empties keeps its items for the unlooted lifetime.
/// </para>
/// <para>
/// There is no client-facing ground drop: loot is always carried by the corpse.
/// </para>
/// <para>
/// The registry is process-wide static state keyed by map and corpse handle, so it needs no dependency injection.
/// </para>
/// </remarks>
public static class GroundItemService
{
 /// <summary>One item lying on a corpse.</summary>
 /// <param name="Uid">The loot uid, which the loot window lists and the looter's pickup names.</param>
 /// <param name="ItemTemplateId">ItemInfo template id.</param>
 /// <param name="Count">Stack count.</param>
 /// <param name="DiceBound">The item goes to a party roll instead of to the looter.</param>
    public sealed record CorpseItem(ulong Uid, uint ItemTemplateId, int Count, bool DiceBound = false);

 /// <summary>A character that may take items off a corpse, and the 64-bit GID its 2374 lists for it.</summary>
    public readonly record struct CorpseLooter(uint CharId, ulong Gid);

 /// <summary>A corpse's loot as it stands: the dead NPC's template, the looters, and the items left.</summary>
 /// <param name="NpcId">NPCInfo id of the dead NPC.</param>
 /// <param name="OwnerCharId">The killer. PartyDiceService takes a rolled item under it.</param>
 /// <param name="OwnerGid">The killer's 64-bit GID.</param>
 /// <param name="Looters">Everyone who may take the items: the killer first, then the party members who share the corpse's loot.</param>
    public sealed record CorpseLoot(uint MapId, uint CorpseHandle, uint NpcId, uint OwnerCharId, ulong OwnerGid,
        DateTime? DeathStamp, IReadOnlyList<CorpseItem> Items, IReadOnlyList<CorpseLooter> Looters);

 /// <summary>Party loot mode "Free for All" (3).</summary>
    public const byte LootModeFreeForAll = 3;

 /// <summary>Party loot mode "Wearer Roll Dice" (7).</summary>
    public const byte LootModeWearerRollDice = 7;

 /// <summary>
 /// True when a kill in a party with <paramref name="lootMode"/> shares the corpse's loot with every member on the
 /// killer's map: Roll Dice (2), Free for All (3) and Wearer Roll Dice (7).
 /// </summary>
    public static bool SharesCorpseLoot(byte lootMode)
        => lootMode is PartyDiceRolls.LootModeDice or LootModeFreeForAll or LootModeWearerRollDice;

 /// <summary>
 /// How long a corpse keeps unlooted items when the kill names no other lifetime: Config_Def type 22, normal
 /// monster looting wait, 120 seconds.
 /// </summary>
    public static readonly TimeSpan UnlootedCorpseLifetime = TimeSpan.FromSeconds(120);

 /// <summary>
 /// Whether kill loot stays on the corpse for the looter to take. When <c>false</c>, each drop goes straight into
 /// the killer's bag at the kill. Defaults to <c>true</c>.
 /// </summary>
    public static bool CorpseLootEnabled { get; set; } = true;

 /// <summary>
 /// High dword of the item instance uids that loot uses (the uid in a 2374 entry, a 1322 request and the 2333 item
 /// record). Loot uids reuse it so they are shaped like real item uids.
 /// </summary>
    public const uint ItemUidHighBand = 0x0E043DA8u;

 /// <summary>Bit position of the high dword in a 64-bit key or uid.</summary>
    private const int HighDwordShift = 32;

    private sealed class CorpseEntry
    {
        public readonly Lock Gate = new();
        public required uint MapId;
        public required uint CorpseHandle;
        public required uint NpcId;
        public required uint OwnerCharId;
        public required ulong OwnerGid;
 /// <summary>The killer first, then the members sharing the loot. Fixed at <see cref="BeginCorpse"/>.</summary>
        public required IReadOnlyList<CorpseLooter> Looters;
        public required DateTime? DeathStamp;
        public required DateTime RegisteredAtUtc;
        public required TimeSpan Lifetime;
        public readonly List<CorpseItem> Items = new();

 /// <summary>When the take of the last item ran (under <see cref="Gate"/>); null while items remain.</summary>
        public DateTime? EmptiedAtUtc;

 /// <summary>Open party rolls on this corpse's items (<see cref="HoldCorpse"/>, under <see cref="Gate"/>);
 /// the corpse does not lapse while any is open.</summary>
        public int Holds;

 /// <summary>
 /// Completed when the corpse is emptied or forgotten, which is what
 /// <see cref="WaitUntilCorpseMayLeaveAsync"/> waits on. Replaced (under <see cref="Gate"/>) when a
 /// returned item loads an emptied corpse again.
 /// </summary>
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Lapsed(DateTime nowUtc)
        {
            lock (Gate) return Holds == 0 && nowUtc - RegisteredAtUtc >= Lifetime;
        }

        public bool MayLoot(uint charId)
        {
            foreach (var looter in Looters)
                if (looter.CharId == charId) return true;
            return false;
        }
    }

    private static readonly ConcurrentDictionary<ulong, CorpseEntry> _corpses = new();
    private static int _corpseUidSeq;

    private static ulong CorpseKey(uint mapId, uint corpseHandle) => ((ulong)mapId << HighDwordShift) | corpseHandle;

 /// <summary>
 /// Starts the loot of a new death. Replaces any earlier registration under the same handle, and drops every
 /// corpse whose lifetime has run out. Call once per kill, before its <see cref="AddCorpseItem"/> calls.
 /// Unlooted items stay for <paramref name="unlootedLifetime"/>, or <see cref="UnlootedCorpseLifetime"/> when it is
 /// null. <paramref name="sharedLooters"/> are the party members who may loot the corpse besides the killer.
 /// </summary>
    public static void BeginCorpse(uint mapId, uint corpseHandle, uint npcId, DateTime? deathStamp,
        uint ownerCharId, ulong ownerGid, DateTime? nowUtc = null, TimeSpan? unlootedLifetime = null,
        IReadOnlyList<CorpseLooter>? sharedLooters = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var kv in _corpses)
            if (kv.Value.Lapsed(now))
                Forget(kv.Key, kv.Value);

        var looters = new List<CorpseLooter> { new(ownerCharId, ownerGid) };
        if (sharedLooters != null)
            foreach (var looter in sharedLooters)
                if (!looters.Exists(l => l.CharId == looter.CharId))
                    looters.Add(looter);

        var key = CorpseKey(mapId, corpseHandle);
        if (_corpses.TryGetValue(key, out var previous)) Forget(key, previous);
        _corpses[key] = new CorpseEntry
        {
            MapId = mapId,
            CorpseHandle = corpseHandle,
            NpcId = npcId,
            OwnerCharId = ownerCharId,
            OwnerGid = ownerGid,
            Looters = looters,
            DeathStamp = deathStamp,
            RegisteredAtUtc = now,
            Lifetime = unlootedLifetime is { } l && l > TimeSpan.Zero ? l : UnlootedCorpseLifetime,
        };
    }

 /// <summary>
 /// Adds a rolled drop to a corpse opened by <see cref="BeginCorpse"/>, under a fresh loot uid, marked for the
 /// party dice when <paramref name="diceBound"/>. Returns null when the corpse was never begun. Sends nothing.
 /// </summary>
    public static CorpseItem? AddCorpseItem(uint mapId, uint corpseHandle, uint itemTemplateId, int count, bool diceBound = false)
    {
        if (itemTemplateId == 0 || count <= 0) return null;
        if (!_corpses.TryGetValue(CorpseKey(mapId, corpseHandle), out var entry)) return null;
        var item = new CorpseItem(NextCorpseUid(), itemTemplateId, count, diceBound);
        lock (entry.Gate) entry.Items.Add(item);
        return item;
    }

    private static ulong NextCorpseUid()
        => ((ulong)ItemUidHighBand << HighDwordShift) | (uint)System.Threading.Interlocked.Increment(ref _corpseUidSeq);

 /// <summary>
 /// The corpse's loot for <paramref name="charId"/>, or false when there is none for them: no entry, a different
 /// death (<paramref name="liveDeathStamp"/> is the NPC's current death time, null while alive), a lapsed corpse,
 /// someone who is not one of its looters, or no items left.
 /// </summary>
    public static bool TryGetCorpseLoot(uint mapId, uint corpseHandle, uint charId, DateTime? liveDeathStamp,
        out CorpseLoot loot, DateTime? nowUtc = null)
    {
        loot = null!;
        if (!TryGetLive(mapId, corpseHandle, liveDeathStamp, nowUtc, out var entry)) return false;
        lock (entry.Gate)
        {
            if (!entry.MayLoot(charId) || entry.Items.Count == 0) return false;
            loot = new CorpseLoot(entry.MapId, entry.CorpseHandle, entry.NpcId, entry.OwnerCharId, entry.OwnerGid,
                entry.DeathStamp, entry.Items.ToArray(), entry.Looters);
            return true;
        }
    }

 /// <summary>True while the corpse still holds items for anyone (the kill path's "was it emptied?").</summary>
    public static bool HasCorpseLoot(uint mapId, uint corpseHandle, DateTime? liveDeathStamp, DateTime? nowUtc = null)
    {
        if (!TryGetLive(mapId, corpseHandle, liveDeathStamp, nowUtc, out var entry)) return false;
        lock (entry.Gate) return entry.Items.Count > 0;
    }

 /// <summary>
 /// Atomically takes the item <paramref name="uid"/> off the corpse for <paramref name="charId"/>, one of the
 /// corpse's looters; a dice award takes it under the killer. Racing takes of the same item grant it once.
 /// <paramref name="itemsLeft"/> is how many items the corpse still holds afterwards (0 means emptied).
 /// </summary>
    public static bool TryTakeCorpseItem(uint mapId, uint corpseHandle, ulong uid, uint charId, DateTime? liveDeathStamp,
        out CorpseItem item, out int itemsLeft, DateTime? nowUtc = null)
    {
        item = null!;
        itemsLeft = 0;
        if (!TryGetLive(mapId, corpseHandle, liveDeathStamp, nowUtc, out var entry)) return false;
        lock (entry.Gate)
        {
            if (!entry.MayLoot(charId)) return false;
            int at = entry.Items.FindIndex(i => i.Uid == uid);
            if (at < 0) return false;
            item = entry.Items[at];
            entry.Items.RemoveAt(at);
            itemsLeft = entry.Items.Count;
            if (itemsLeft == 0)
            {
                entry.EmptiedAtUtc = nowUtc ?? DateTime.UtcNow;
                entry.Changed.TrySetResult();
            }
            return true;
        }
    }

 /// <summary>Puts a taken item back (the bag write failed after the take), unless the corpse is gone.</summary>
    public static void ReturnCorpseItem(uint mapId, uint corpseHandle, CorpseItem item)
    {
        if (_corpses.TryGetValue(CorpseKey(mapId, corpseHandle), out var entry))
            lock (entry.Gate)
            {
                if (entry.Items.Any(i => i.Uid == item.Uid)) return;
                entry.Items.Add(item);
                if (entry.EmptiedAtUtc != null)
                {
                    
                    entry.EmptiedAtUtc = null;
                    entry.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
    }

 /// <summary>
 /// Clears the dice mark of item <paramref name="uid"/>, which stays on the corpse after a roll that every
 /// participant passed, so the looter's next pickup takes it. False when the corpse or the item is gone.
 /// </summary>
    public static bool ReleaseDiceBound(uint mapId, uint corpseHandle, ulong uid)
    {
        if (!_corpses.TryGetValue(CorpseKey(mapId, corpseHandle), out var entry)) return false;
        lock (entry.Gate)
        {
            int at = entry.Items.FindIndex(i => i.Uid == uid);
            if (at < 0) return false;
            entry.Items[at] = entry.Items[at] with { DiceBound = false };
            return true;
        }
    }

 /// <summary>
 /// Keeps the corpse from lapsing while a party roll on one of its items is open, so the award can still take the
 /// item. A roll can outlast the unlooted lifetime. Every hold ends with <see cref="ReleaseCorpse"/>. False when
 /// the corpse is gone.
 /// </summary>
    public static bool HoldCorpse(uint mapId, uint corpseHandle)
    {
        if (!_corpses.TryGetValue(CorpseKey(mapId, corpseHandle), out var entry)) return false;
        lock (entry.Gate) entry.Holds++;
        return true;
    }

 /// <summary>Ends one <see cref="HoldCorpse"/>, and wakes the corpse's despawn so it looks again.</summary>
    public static void ReleaseCorpse(uint mapId, uint corpseHandle)
    {
        if (!_corpses.TryGetValue(CorpseKey(mapId, corpseHandle), out var entry)) return;
        lock (entry.Gate)
        {
            if (entry.Holds > 0) entry.Holds--;
            if (entry.EmptiedAtUtc == null)
            {
                var woken = entry.Changed;
                entry.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                woken.TrySetResult();
            }
        }
    }

 /// <summary>Forgets a corpse.</summary>
    public static void RemoveCorpse(uint mapId, uint corpseHandle)
    {
        var key = CorpseKey(mapId, corpseHandle);
        if (_corpses.TryGetValue(key, out var entry)) Forget(key, entry);
    }

 /// <summary>Drops <paramref name="entry"/> (only if it is still the one under <paramref name="key"/>) and
 /// wakes a despawn waiting on it.</summary>
    private static void Forget(ulong key, CorpseEntry entry)
    {
        _corpses.TryRemove(new KeyValuePair<ulong, CorpseEntry>(key, entry));
        lock (entry.Gate) entry.Changed.TrySetResult();
    }

 /// <summary>
 /// Waits until the corpse of the death stamped <paramref name="deathStamp"/> may leave the clients, then forgets
 /// its loot. The wait ends at once if the loot is forgotten meanwhile.
 /// </summary>
 /// <param name="corpseDisappear">The dead NPC's NPCInfo Corpse_Disappear_Time.</param>
 /// <remarks>
 /// The wait depends on the loot left on the corpse. With nothing left at the kill, the corpse leaves
 /// <paramref name="corpseDisappear"/> after the kill. With items, it leaves <paramref name="corpseDisappear"/>
 /// after the last item is taken. With items still on it when the unlooted lifetime ends, it leaves
 /// <paramref name="corpseDisappear"/> after that lifetime. A respawn does not end the wait.
 /// </remarks>
    public static async Task WaitUntilCorpseMayLeaveAsync(uint mapId, uint corpseHandle, DateTime? deathStamp,
        TimeSpan corpseDisappear)
    {
        if (corpseDisappear < TimeSpan.Zero) corpseDisappear = TimeSpan.Zero;
        var key = CorpseKey(mapId, corpseHandle);
        if (!_corpses.TryGetValue(key, out var entry) || entry.DeathStamp != deathStamp)
        {
            await DelayAsync(corpseDisappear);
            return;
        }

        
        var heldPastLapse = false;
        try
        {
            while (true)
            {
                Task changed;
                DateTime? emptiedAt;
                int left, holds;
                lock (entry.Gate)
                {
                    changed = entry.Changed.Task;
                    emptiedAt = entry.EmptiedAtUtc;
                    left = entry.Items.Count;
                    holds = entry.Holds;
                }

                if (left == 0)
                {
                    
                    
                    var from = emptiedAt ?? entry.RegisteredAtUtc;
                    await DelayAsync(from + corpseDisappear - DateTime.UtcNow);

                    
                    
                    bool reloaded;
                    lock (entry.Gate) reloaded = entry.Items.Count > 0;
                    if (reloaded && _corpses.TryGetValue(key, out var still) && ReferenceEquals(still, entry)) continue;
                    return;
                }

                if (!_corpses.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return; 
                var lapse = entry.RegisteredAtUtc + entry.Lifetime - DateTime.UtcNow;
                if (lapse <= TimeSpan.Zero)
                {
                    if (holds == 0)
                    {
                        
                        
                        
                        if (!heldPastLapse)
                            await DelayAsync(entry.RegisteredAtUtc + entry.Lifetime + corpseDisappear - DateTime.UtcNow);
                        return;
                    }
                    
                    heldPastLapse = true;
                    await changed;
                    continue;
                }

                await Task.WhenAny(changed, DelayAsync(lapse));
            }
        }
        finally
        {
            Forget(key, entry);
        }
    }

 /// <summary>A delay that ends early, without throwing, once <paramref name="delay"/> elapses.</summary>
    private static async Task DelayAsync(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
    }

 /// <summary>
 /// The 2374 answer to a 1338 on this corpse, when it holds items for <paramref name="charId"/>: every item left,
 /// including rolled ones, with the requester as the only looter listed. On a shared corpse that is the member
 /// who asked, not the killer. Returns false when there is nothing to list for them.
 /// </summary>
    public static bool TryBuildCorpseList2374(uint mapId, uint corpseHandle, uint charId, DateTime? liveDeathStamp,
        out byte[] body)
    {
        body = Array.Empty<byte>();
        if (!TryGetCorpseLoot(mapId, corpseHandle, charId, liveDeathStamp, out var loot)) return false;
        var requester = loot.Looters.First(l => l.CharId == charId);
        body = PickupNotifyPackets.BuildHeartBeatItem2374(corpseHandle, loot.Items, new[] { requester.Gid });
        return true;
    }

    private static bool TryGetLive(uint mapId, uint corpseHandle, DateTime? liveDeathStamp, DateTime? nowUtc, out CorpseEntry entry)
    {
        var key = CorpseKey(mapId, corpseHandle);
        if (!_corpses.TryGetValue(key, out entry!)) return false;
        var now = nowUtc ?? DateTime.UtcNow;
        if (liveDeathStamp is null || entry.DeathStamp != liveDeathStamp || entry.Lapsed(now))
        {
            
            Forget(key, entry);
            return false;
        }
        return true;
    }
}
