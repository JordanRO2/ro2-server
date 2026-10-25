using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Opens the dungeon finder's party role round (<see cref="DungeonFinderPartyRound"/>) for a search by a party
/// member, and schedules its timeout. Used by the 1555 and 1556 searches.
/// </summary>
/// <remarks>
/// <para>
/// A search that does not qualify for a party round (no party, no other member online, or a party-size list
/// that does not match) takes the solo path. A search while already queued, or while a round is open, is
/// refused with result 9.
/// </para>
/// <para>
/// A round that is opened gets its expiry after <see cref="DungeonFinderPartyRound.SelectRoleTimeout"/>.
/// </para>
/// </remarks>
public static class DungeonFinderPartyRoundStarter
{
 /// <summary>
 /// Handles the party path for a search by <paramref name="player"/>. Returns true when the search was answered
 /// here (round opened, or refused), false when the solo path must run.
 /// <paramref name="answerOpcode"/> is the opcode the searcher's answer is sent on: 2804 for 1555, 2805 for 1556.
 /// </summary>
    public static async Task<bool> TryOpenAsync(ProudSession session, PlayerState player, byte roleFlags,
        List<FindDungeonList> rows, GameDataStore data, DungeonFinderQueue queue, IServiceProvider services,
        PlayerStateManager players, BroadcastService broadcast, ILogger logger, ushort answerOpcode)
    {
        if (!rows.TrueForAll(r => r.Party_Num > 1)) return false;
        var parties = services.GetRequiredService<PartyService>();
        if (parties.GetPlayerParty(session.SessionId) is not { } party) return false;
        var others = new List<PlayerState>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
            if (memberId != session.SessionId && parties.GetOnlineMember(party, memberId) is { } member)
                others.Add(member);
        if (others.Count == 0) return false;

        
        
        if (data.FindDungeonList.Any(r => queue.MembersOf(r.List_ID).Exists(e => ReferenceEquals(e.Player, player))))
        {
            await session.SendInternalRmiAsync(answerOpcode,
                DungeonFinderPartyRound.SearcherAnswer(answerOpcode, DungeonFinderPackets.ResultAlreadyRegistered));
            return true;
        }

        
        var isSeated = DungeonFinderPartyRound.SeatCheckFor(players, parties);
        var opened = DungeonFinderPartyRound.Shared.Open(party.PartyId, rows[0], player, roleFlags, others, isSeated,
            answerOpcode);
        await DungeonFinderPartyRound.DeliverAsync(opened.Sends, broadcast.GetSession);
        switch (opened.Status)
        {
            case DungeonFinderPartyRound.OpenStatus.AlreadyOpen:
                await session.SendInternalRmiAsync(answerOpcode,
                    DungeonFinderPartyRound.SearcherAnswer(answerOpcode, DungeonFinderPackets.ResultAlreadyRegistered));
                return true;
            case DungeonFinderPartyRound.OpenStatus.Opened:
                
                _ = ExpireRoundWhenDueAsync(opened.RoundId, isSeated, broadcast, logger);
                logger.LogInformation("{Name} opened dungeon finder party round {Round} for lists {Lists} as role {Role}, asking {Count} member(s)",
                    player.Name, opened.RoundId, string.Join(",", rows.Select(r => r.List_ID)), opened.SearcherRole, others.Count);
                return true;
            default:
                return false;
        }
    }

 /// <summary>
 /// Ends round <paramref name="roundId"/> after its timeout when it is still open, sending the closing notice to
 /// every member. A round that already ended is left alone.
 /// </summary>
    private static async Task ExpireRoundWhenDueAsync(long roundId, DungeonFinderPartyRound.SeatCheck isSeated,
        BroadcastService broadcast, ILogger logger)
    {
        try
        {
            await Task.Delay(DungeonFinderPartyRound.SelectRoleTimeout);
            var expired = DungeonFinderPartyRound.Shared.Expire(roundId, isSeated);
            await DungeonFinderPartyRound.DeliverAsync(expired.Sends, broadcast.GetSession);
            if (expired.Ended)
                logger.LogInformation("Dungeon finder party round {Round} ended at its timeout (a role never picked, or a member gone)", roundId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dungeon finder party round {Round}: ending it on its timeout failed", roundId);
        }
    }
}
