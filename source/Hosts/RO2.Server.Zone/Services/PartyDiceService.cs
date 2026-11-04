using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Parties.Application;
using RO2.PartyDice.Application;
using RO2.PartyDice.Domain;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

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
/// The decisions (who answered, who wins, whether the dice mark is released or kept) are the <see cref="DiceRound"/>
/// aggregate's. This host service keeps the open rolls, the per-roll gate, the settle timer, the liveness reads, the
/// sends and the award. Opened rolls are held here because they are created by one session's request and settled by
/// a timer.
/// </para>
/// </remarks>
public sealed class PartyDiceService(PartyService parties, IPartyRoster roster, PlayerStateManager players,
    BroadcastService broadcast, NpcStateManager npcs, GameDataStore data, IServiceScopeFactory scopes,
    ILogger<PartyDiceService> logger)
{
 /// <summary>
 /// Time after which an open roll settles without a response from the remaining participants. This matches the
 /// client's own automatic pass.
 /// </summary>
    public static readonly TimeSpan AutoPassAfter = TimeSpan.FromMilliseconds(180_000);

    private readonly TimeSpan _window = AutoPassAfter;

 /// <summary>Test seam: a shorter settle window so the timeout settle can be driven.</summary>
    internal PartyDiceService(PartyService parties, IPartyRoster roster, PlayerStateManager players,
        BroadcastService broadcast, NpcStateManager npcs, GameDataStore data, IServiceScopeFactory scopes,
        ILogger<PartyDiceService> logger, TimeSpan window)
        : this(parties, roster, players, broadcast, npcs, data, scopes, logger) => _window = window;

    private sealed class Roulette
    {
        public readonly object Gate = new();
        public required GroundItemService.CorpseLoot Corpse;
        public required GroundItemService.CorpseItem Item;
        public required uint PartyId;
        public required DiceRound State;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

 /// <summary>Open rolls, keyed by corpse item uid.</summary>
    private readonly ConcurrentDictionary<ulong, Roulette> _open = new();

 /// <summary>True while a roll for the corpse item <paramref name="uid"/> is open.</summary>
    public bool IsRolling(ulong uid) => _open.ContainsKey(uid);

 /// <summary>
 /// Opens a roll for <paramref name="item"/> on <paramref name="corpse"/> for the requester's party, and sends 2468 to
 /// every participant. Does nothing when a roll for this item is already open.
 /// </summary>
    internal async Task OpenAsync(PlayerState requester, uint partyId,
        GroundItemService.CorpseLoot corpse, GroundItemService.CorpseItem item)
    {
        var seats = PartyDiceRoster.Seats(roster.RosterOfParty(partyId), corpse.MapId, id => broadcast.GetSession(id) != null);
        if (seats.Count == 0) return;

        var roulette = new Roulette { Corpse = corpse, Item = item, PartyId = partyId, State = new DiceRound(seats) };
        if (!_open.TryAdd(item.Uid, roulette)) return;


        if (!GroundItemService.HoldCorpse(corpse.MapId, corpse.CorpseHandle))
        {
            _open.TryRemove(new KeyValuePair<ulong, Roulette>(item.Uid, roulette));
            return;
        }

        foreach (var effect in roulette.State.Open())
        {
            if (PartyDiceFrames.ToFrame(effect, item) is not { } frame || effect is not Opened opened) continue;
            foreach (var sessionId in opened.ToSessionIds)
                if (broadcast.GetSession(sessionId) is { } s)
                    // One failed 2468 must not abort the open: the settle timer below has to arm so the hold is released.
                    try { await s.SendInternalRmiAsync(frame.Opcode, frame.Body); }
                    catch (Exception ex) { logger.LogWarning(ex, "Party roll 2468 to session {Session} failed", sessionId); }
        }

        logger.LogInformation("{Name} opened the party roll for {Count}x item {Item} (uid {Uid:X}) on corpse {Corpse:X}: {N} participant(s)",
            requester.Name, item.Count, item.ItemTemplateId, item.Uid, corpse.CorpseHandle, seats.Count);

        _ = SettleWhenDueAsync(roulette);
    }

 /// <summary>
 /// Handles a participant's 1380: a roll (choice 0 or 1) or a pass (choice 2). The result is announced to the party,
 /// and the item is awarded once every participant has answered. Answers for an item with no open roll, from a
 /// non-participant, a second answer or an unknown choice are ignored.
 /// </summary>
    internal async Task AnswerAsync(PlayerState player, ulong uid, byte choice)
    {
        if (!_open.TryGetValue(uid, out var r))
        {
            logger.LogDebug("{Name}: 1380 for uid {Uid:X}, which has no open roll — not answered", player.Name, uid);
            return;
        }

        var gid = PartyAnswerPackets.MemberGid(player);
        IReadOnlyList<DiceEffect> effects;
        lock (r.Gate) effects = DiceRolling.Answer(r.State, player.SessionId, gid, choice);
        if (effects.Count == 0) return;

        var party = parties.GetPlayerParty(player.SessionId);
        if (party != null && party.PartyId == r.PartyId
            && PartyDiceFrames.ToFrame(effects[0], r.Item) is { } frame)
            await parties.BroadcastBodyToPartyAsync(party, frame.Opcode, frame.Body);
        logger.LogDebug("{Name} {What} for item {Item} (uid {Uid:X})",
            player.Name, effects[0] is Answered a ? $"rolled {a.Roll}" : "passed", r.Item.ItemTemplateId, uid);

        await TrySettleAsync(r, timedOut: false);
    }

 /// <summary>Settles <paramref name="r"/> at the settle window unless the answers have already settled it.</summary>
    private async Task SettleWhenDueAsync(Roulette r)
    {
        try
        {


            await r.Done.Task.WaitAsync(_window).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!r.Done.Task.IsCompleted)
                await TrySettleAsync(r, timedOut: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Party roll fallback for uid {Uid:X} failed", r.Item.Uid);
        }
    }

 /// <summary>
 /// Settles <paramref name="r"/> when every participant still taking part has answered, or when
 /// <paramref name="timedOut"/> is true. The highest roll wins; when there is no roll, the all-pass notice is sent.
 /// </summary>
    private async Task TrySettleAsync(Roulette r, bool timedOut)
    {
        IReadOnlyList<DiceEffect> effects;
        lock (r.Gate) effects = r.State.Settle(PartyDiceRoster.StillIn(roster, r.PartyId), timedOut);
        if (effects.Count == 0) return;
        _open.TryRemove(new KeyValuePair<ulong, Roulette>(r.Item.Uid, r));
        r.Done.TrySetResult();

        try
        {
            var party = FindParty(r);
            foreach (var effect in effects)
            {
                switch (effect)
                {
                    case AllPassed:
                        // A throwing broadcast must not skip the ReleaseDiceBound that follows.
                        try
                        {
                            if (party != null && PartyDiceFrames.ToFrame(effect, r.Item) is { } frame)
                                await parties.BroadcastBodyToPartyAsync(party, frame.Opcode, frame.Body);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Party roll all-pass notice for uid {Uid:X} failed", r.Item.Uid);
                        }
                        break;
                    case ReleaseDiceBound:
                        GroundItemService.ReleaseDiceBound(r.Corpse.MapId, r.Corpse.CorpseHandle, r.Item.Uid);
                        logger.LogInformation("Party roll for item {Item} (uid {Uid:X}): everyone passed{Why}, it stays on corpse {Corpse:X}",
                            r.Item.ItemTemplateId, r.Item.Uid, timedOut ? " or never answered" : "", r.Corpse.CorpseHandle);
                        break;
                    case Winner winner:
                        await AwardAsync(r, winner, party);
                        break;
                    case HoldDiceBound:
                        break; // the item keeps its dice mark; a successful award takes it off the corpse
                }
            }
        }
        finally
        {

            GroundItemService.ReleaseCorpse(r.Corpse.MapId, r.Corpse.CorpseHandle);
        }
    }

 /// <summary>Finds the roll's party through one of its participants, since a disband replaces the party state.</summary>
    private PartyState? FindParty(Roulette r)
    {
        foreach (var seat in r.State.Seats)
            if (parties.GetPlayerParty(seat.SessionId) is { } party && party.PartyId == r.PartyId)
                return party;
        return null;
    }

    private async Task AwardAsync(Roulette r, Winner winnerSlot, PartyState? party)
    {
        var corpse = r.Corpse;
        var winner = players.GetPlayer(winnerSlot.SessionId);
        var session = broadcast.GetSession(winnerSlot.SessionId);
        if (winner == null || session == null) return;


        var npc = npcs.GetNpc(corpse.CorpseHandle);
        DateTime? liveStamp = npc is { IsDead: true } ? npc.DeathTime : null;

        try
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var owned = await inventory.GetByCharacterAsync(winner.CharacterId);
            var landing = ItemPickUpHandler.PlanLanding(owned, winner.CharacterId, r.Item.ItemTemplateId, r.Item.Count, data.Items);
            if (landing.Refused)
            {
                await ItemPickUpHandler.SendBagFullAsync(session);
                logger.LogInformation("{Name} won item {Item} (uid {Uid:X}) with a full bag; it stays on corpse {Corpse:X}",
                    winner.Name, r.Item.ItemTemplateId, r.Item.Uid, corpse.CorpseHandle);
                return;
            }

            if (!GroundItemService.TryTakeCorpseItem(corpse.MapId, corpse.CorpseHandle, r.Item.Uid, corpse.OwnerCharId,
                    liveStamp, out var item, out var itemsLeft))
            {
                logger.LogInformation("Party roll for uid {Uid:X}: corpse {Corpse:X} no longer holds it; nothing awarded",
                    r.Item.Uid, corpse.CorpseHandle);
                return;
            }

            var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, winner, item.ItemTemplateId, logger);
            if (landed.Count == 0)
            {
                GroundItemService.ReturnCorpseItem(corpse.MapId, corpse.CorpseHandle, item);
                return;
            }


            var looter = players.GetPlayerByCharacterId(corpse.OwnerCharId);
            var looterSession = looter == null ? null : broadcast.GetSession(looter.SessionId);
            if (looterSession != null
                && ItemPickUpHandler.ItemsLooterCanStillTake(corpse.MapId, corpse.CorpseHandle, corpse.OwnerCharId, liveStamp, IsRolling) == 0)
                await looterSession.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                    CombatPackets.BuildLootingAuthority2471(corpse.CorpseHandle, granted: false));

            await ItemPickUpHandler.NotifyLandedAsync(session, winner, services, broadcast, logger, landed,
                item.Uid, item.ItemTemplateId, item.Count);

            if (party != null)
                await parties.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyGetItem,
                    PartyLootNotifyPackets.BuildPartyNotifyGetItem2362(winnerSlot.Gid, item.Uid, corpse.CorpseHandle,
                        item.ItemTemplateId, trailing: (byte)Math.Clamp(itemsLeft, 0, byte.MaxValue)));


            if (itemsLeft == 0 && looterSession != null
                && (data.Npcs.GetValueOrDefault(corpse.NpcId)?.AfterDeath_FuncObject_ID ?? 0) != 0)
                await looterSession.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyCanGatherDeadNpc,
                    CorpseGatherPackets.BuildNotifyCanGatherDeadNpc2534(corpse.CorpseHandle));

            logger.LogInformation("{Name} won {Count}x item {Item} (roll {Roll}) from corpse {Corpse:X} ({Left} left)",
                winner.Name, item.Count, item.ItemTemplateId, winnerSlot.Roll, corpse.CorpseHandle, itemsLeft);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Party roll award of uid {Uid:X} to {Name} failed", r.Item.Uid, winner.Name);
        }
    }
}
