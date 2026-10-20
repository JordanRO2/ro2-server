using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Party loot dice ("roulette"): a corpse item that the party rolls for. The roll is opened with 2468, each member
/// answers with 1380 (a roll, reported by 2469, or a pass, reported by 2470), and the item is awarded to the highest roll.
/// </summary>
/// <remarks>
/// <para>
/// A roll opens when the looter picks up an item that the party loot rule marks for dice, or when a member requests
/// it with 1379. While the roll is open, the item stays on the corpse.
/// </para>
/// <para>
/// Only the party members on the corpse's map when the roll opens take part. The roll settles when the last
/// participant has answered, or after <see cref="AutoPassAfter"/>, when the missing answers count as passes.
/// </para>
/// <para>
/// The winner receives the item through the normal pickup path, and the party is notified of the award. A winner
/// with a full bag keeps the item on the corpse, still marked for dice. After an all-pass, the item loses its dice
/// mark and the looter can take it normally.
/// </para>
/// <para>
/// Opened rolls are held in static state, because they are created by one session's request and settled by a timer.
/// </para>
/// </remarks>
public static class PartyDiceService
{
 /// <summary>
 /// Time after which an open roll settles without a response from the remaining participants. This matches the
 /// client's own automatic pass.
 /// </summary>
    public static readonly TimeSpan AutoPassAfter = TimeSpan.FromMilliseconds(180_000);

 /// <summary>The services a roll needs, taken from the request's provider.</summary>
    internal sealed record Deps(PartyService Party, PlayerStateManager Players, BroadcastService Broadcast,
        NpcStateManager Npcs, GameDataStore Data, IServiceScopeFactory Scopes, ILogger Logger)
    {
        internal static Deps From(IServiceProvider services, ILogger logger) => new(
            services.GetRequiredService<PartyService>(),
            services.GetRequiredService<PlayerStateManager>(),
            services.GetRequiredService<BroadcastService>(),
            services.GetRequiredService<NpcStateManager>(),
            services.GetRequiredService<GameDataStore>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            logger);
    }

    private sealed class Participant
    {
        public required uint SessionId;
        public required ulong Gid;
        public bool Answered;
        public bool Rolled;
        public ushort Roll;
        public long AnswerOrder;
    }

    private sealed class Roulette
    {
        public readonly object Gate = new();
        public required GroundItemService.CorpseLoot Corpse;
        public required GroundItemService.CorpseItem Item;
        public required uint PartyId;
        public required List<Participant> Participants;
        public long NextOrder;
        public bool Settled;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

 /// <summary>Open rolls, keyed by corpse item uid.</summary>
    private static readonly ConcurrentDictionary<ulong, Roulette> _open = new();

 /// <summary>True while a roll for the corpse item <paramref name="uid"/> is open.</summary>
    public static bool IsRolling(ulong uid) => _open.ContainsKey(uid);

 /// <summary>
 /// Opens a roll for <paramref name="item"/> on <paramref name="corpse"/> for the requester's party, and sends 2468 to
 /// every participant. Does nothing when a roll for this item is already open.
 /// </summary>
    internal static async Task OpenAsync(Deps d, PlayerState requester, PartyState party,
        GroundItemService.CorpseLoot corpse, GroundItemService.CorpseItem item)
    {
        var participants = new List<Participant>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            var member = d.Party.GetOnlineMember(party, memberId);
            if (member == null || member.MapId != corpse.MapId || d.Broadcast.GetSession(memberId) == null) continue;
            participants.Add(new Participant { SessionId = memberId, Gid = PartyAnswerPackets.MemberGid(member) });
        }
        if (participants.Count == 0) return; 

        var roulette = new Roulette { Corpse = corpse, Item = item, PartyId = party.PartyId, Participants = participants };
        if (!_open.TryAdd(item.Uid, roulette)) return;
        
        
        if (!GroundItemService.HoldCorpse(corpse.MapId, corpse.CorpseHandle))
        {
            _open.TryRemove(new KeyValuePair<ulong, Roulette>(item.Uid, roulette));
            return;
        }

        var open = PartyDicePackets.BuildRouletteOpen2468(item.Uid, item.ItemTemplateId, item.Count);
        foreach (var p in participants)
            if (d.Broadcast.GetSession(p.SessionId) is { } s)
                await s.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingBoxRouletteOpen, open);

        d.Logger.LogInformation("{Name} opened the party roll for {Count}x item {Item} (uid {Uid:X}) on corpse {Corpse:X}: {N} participant(s)",
            requester.Name, item.Count, item.ItemTemplateId, item.Uid, corpse.CorpseHandle, participants.Count);

        _ = SettleWhenDueAsync(d, roulette);
    }

 /// <summary>
 /// Handles a participant's 1380: a roll (choice 0 or 1) or a pass (choice 2). The result is announced to the party,
 /// and the item is awarded once every participant has answered. Answers for an item with no open roll, from a
 /// non-participant, a second answer or an unknown choice are ignored.
 /// </summary>
    internal static async Task AnswerAsync(Deps d, PlayerState player, ulong uid, byte choice)
    {
        if (!_open.TryGetValue(uid, out var r))
        {
            d.Logger.LogDebug("{Name}: 1380 for uid {Uid:X}, which has no open roll — not answered", player.Name, uid);
            return;
        }

        var gid = PartyAnswerPackets.MemberGid(player);
        bool rolled;
        ushort roll;
        lock (r.Gate)
        {
            var me = r.Participants.FirstOrDefault(p => p.SessionId == player.SessionId && p.Gid == gid);
            if (r.Settled || me == null || me.Answered) return;
            rolled = PartyDiceRolls.TryRoll(choice, Random.Shared, out roll);
            if (!rolled && choice != PartyDiceRolls.ChoicePass) return;
            me.Answered = true;
            me.Rolled = rolled;
            me.Roll = roll;
            me.AnswerOrder = r.NextOrder++;
        }

        var party = d.Party.GetPlayerParty(player.SessionId);
        if (party != null && party.PartyId == r.PartyId)
        {
            var body = rolled
                ? PartyDicePackets.BuildRouletteResult2469(0, roll, gid, r.Item.ItemTemplateId, uid)
                : PartyDicePackets.BuildRouletteRefuse2470(gid, r.Item.ItemTemplateId, uid);
            await d.Party.BroadcastBodyToPartyAsync(party,
                rolled ? ZoneS2COpcodes.OnLootingBoxRouletteResult : ZoneS2COpcodes.OnLootingBoxRouletteRefuse, body);
        }
        d.Logger.LogDebug("{Name} {What} for item {Item} (uid {Uid:X})",
            player.Name, rolled ? $"rolled {roll}" : "passed", r.Item.ItemTemplateId, uid);

        await TrySettleAsync(d, r, timedOut: false);
    }

 /// <summary>Settles <paramref name="r"/> at <see cref="AutoPassAfter"/> unless the answers have already settled it.</summary>
    private static async Task SettleWhenDueAsync(Deps d, Roulette r)
    {
        try
        {
            
            
            
            await r.Done.Task.WaitAsync(AutoPassAfter).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!r.Done.Task.IsCompleted)
                await TrySettleAsync(d, r, timedOut: true);
        }
        catch (Exception ex)
        {
            d.Logger.LogWarning(ex, "Party roll fallback for uid {Uid:X} failed", r.Item.Uid);
        }
    }

 /// <summary>True when the participant is still in the roll's party and online as the character that was asked.</summary>
    private static bool StillIn(Deps d, Roulette r, Participant p)
    {
        var party = d.Party.GetPlayerParty(p.SessionId);
        return party != null && party.PartyId == r.PartyId
            && d.Party.GetOnlineMember(party, p.SessionId) is { } live && PartyAnswerPackets.MemberGid(live) == p.Gid;
    }

 /// <summary>
 /// Settles <paramref name="r"/> when every participant still taking part has answered, or when
 /// <paramref name="timedOut"/> is true. The highest roll wins; when there is no roll, the all-pass notice is sent.
 /// </summary>
    private static async Task TrySettleAsync(Deps d, Roulette r, bool timedOut)
    {
        (ushort Roll, long AnswerOrder, Participant P)[] rolls;
        lock (r.Gate)
        {
            if (r.Settled) return;
            if (!timedOut && r.Participants.Any(p => !p.Answered && StillIn(d, r, p))) return;
            r.Settled = true;
            rolls = r.Participants.Where(p => p.Rolled && StillIn(d, r, p)).Select(p => (p.Roll, p.AnswerOrder, p)).ToArray();
        }
        _open.TryRemove(new KeyValuePair<ulong, Roulette>(r.Item.Uid, r));
        r.Done.TrySetResult();

        try
        {
            var party = FindParty(d, r);
            int best = PartyDiceRolls.PickWinner(rolls.Select(x => (x.Roll, x.AnswerOrder)).ToArray());
            if (best < 0)
            {
                if (party != null)
                    await d.Party.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnLootingBoxRouletteResult,
                        PartyDicePackets.BuildRouletteResult2469(PartyDicePackets.ResultAllPassed, 0, 0,
                            r.Item.ItemTemplateId, r.Item.Uid));
                GroundItemService.ReleaseDiceBound(r.Corpse.MapId, r.Corpse.CorpseHandle, r.Item.Uid);
                d.Logger.LogInformation("Party roll for item {Item} (uid {Uid:X}): everyone passed{Why}, it stays on corpse {Corpse:X}",
                    r.Item.ItemTemplateId, r.Item.Uid, timedOut ? " or never answered" : "", r.Corpse.CorpseHandle);
                return;
            }

            await AwardAsync(d, r, rolls[best].P, party);
        }
        finally
        {
            
            GroundItemService.ReleaseCorpse(r.Corpse.MapId, r.Corpse.CorpseHandle);
        }
    }

 /// <summary>Finds the roll's party through one of its participants, since a disband replaces the party state.</summary>
    private static PartyState? FindParty(Deps d, Roulette r)
    {
        foreach (var p in r.Participants)
            if (d.Party.GetPlayerParty(p.SessionId) is { } party && party.PartyId == r.PartyId)
                return party;
        return null;
    }

    private static async Task AwardAsync(Deps d, Roulette r, Participant winnerSlot, PartyState? party)
    {
        var corpse = r.Corpse;
        var winner = d.Players.GetPlayer(winnerSlot.SessionId);
        var session = d.Broadcast.GetSession(winnerSlot.SessionId);
        if (winner == null || session == null) return;

        
        
        var npc = d.Npcs.GetNpc(corpse.CorpseHandle);
        DateTime? liveStamp = npc is { IsDead: true } ? npc.DeathTime : null;

        try
        {
            using var scope = d.Scopes.CreateScope();
            var services = scope.ServiceProvider;
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var owned = await inventory.GetByCharacterAsync(winner.CharacterId);
            var landing = ItemPickUpHandler.PlanLanding(owned, winner.CharacterId, r.Item.ItemTemplateId, r.Item.Count, d.Data.Items);
            if (landing.Refused)
            {
                await ItemPickUpHandler.SendBagFullAsync(session);
                d.Logger.LogInformation("{Name} won item {Item} (uid {Uid:X}) with a full bag; it stays on corpse {Corpse:X}",
                    winner.Name, r.Item.ItemTemplateId, r.Item.Uid, corpse.CorpseHandle);
                return;
            }

            if (!GroundItemService.TryTakeCorpseItem(corpse.MapId, corpse.CorpseHandle, r.Item.Uid, corpse.OwnerCharId,
                    liveStamp, out var item, out var itemsLeft))
            {
                d.Logger.LogInformation("Party roll for uid {Uid:X}: corpse {Corpse:X} no longer holds it; nothing awarded",
                    r.Item.Uid, corpse.CorpseHandle);
                return;
            }

            var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, winner, item.ItemTemplateId, d.Logger);
            if (landed.Count == 0)
            {
                GroundItemService.ReturnCorpseItem(corpse.MapId, corpse.CorpseHandle, item);
                return;
            }

            
            var looter = d.Players.GetPlayerByCharacterId(corpse.OwnerCharId);
            var looterSession = looter == null ? null : d.Broadcast.GetSession(looter.SessionId);
            if (looterSession != null
                && ItemPickUpHandler.ItemsLooterCanStillTake(corpse.MapId, corpse.CorpseHandle, corpse.OwnerCharId, liveStamp) == 0)
                await looterSession.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                    CombatPackets.BuildLootingAuthority2471(corpse.CorpseHandle, granted: false));

            await ItemPickUpHandler.NotifyLandedAsync(session, winner, services, d.Broadcast, d.Logger, landed,
                item.Uid, item.ItemTemplateId, item.Count);

            if (party != null)
                await d.Party.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyGetItem,
                    PartyLootNotifyPackets.BuildPartyNotifyGetItem2362(winnerSlot.Gid, item.Uid, corpse.CorpseHandle,
                        item.ItemTemplateId, trailing: (byte)Math.Clamp(itemsLeft, 0, byte.MaxValue)));

            
            if (itemsLeft == 0 && looterSession != null
                && (d.Data.Npcs.GetValueOrDefault(corpse.NpcId)?.AfterDeath_FuncObject_ID ?? 0) != 0)
                await looterSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc,
                    CorpseGatherPackets.BuildNotifyCanGatherDeadNpc2534(corpse.CorpseHandle));

            d.Logger.LogInformation("{Name} won {Count}x item {Item} (roll {Roll}) from corpse {Corpse:X} ({Left} left)",
                winner.Name, item.Count, item.ItemTemplateId, winnerSlot.Roll, corpse.CorpseHandle, itemsLeft);
        }
        catch (Exception ex)
        {
            d.Logger.LogWarning(ex, "Party roll award of uid {Uid:X} to {Name} failed", r.Item.Uid, winner.Name);
        }
    }
}
