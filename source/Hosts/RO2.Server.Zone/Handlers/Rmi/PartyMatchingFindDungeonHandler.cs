using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1555 ReqPartyMatchingFindDungeon: start a dungeon finder search. Answered with 2804, the 2806 queue state
/// and, for a list the searcher fills alone, the 2808 match prompt, all in the same millisecond.
/// </summary>
/// <remarks>
/// <para>
/// Body (U32 family, <see cref="DungeonFinderPackets.TryParseFindDungeon1555"/>): <c>[u32 7][u8 role flags]
/// [u16 count][count x u32 List_ID]</c>.
/// </para>
/// <para>
/// Behaviour:
/// <list type="bullet">
/// <item>List 21 (Party_Num 1): 2804 <c>0000</c>, 2806 <c>0000 15000000 00 01 00 02</c> and 2808
/// <c>15000000 02 00</c>, right after the 1555. The client
/// answers the 2808 itself with 1559 [01], see PartyMatchingFindDungeonJoinSelectBtnHandler.</item>
/// <item>List 4 (Wolf Den Hard, Party_Num 5): 2804 and 2806 <c>0000 04000000 00 01 00 02</c> only;
/// the player later cancels with 1558.</item>
/// <item>The order of 2804 and 2806 inside the millisecond varies; the consumers do not depend on it.</item>
/// <item>List 1 (levels 8-14): 1555 is answered with 2804 <c>1100</c> = 17, string 2741 "Your level
/// incorrect to use.", and nothing else. The level tested is the plain character level: a level 50 player
/// with profession level 40 is accepted for list 21 (Max_Level 50).</item>
/// </list>
/// The queue itself, the counts and who gets a match are <see cref="DungeonFinderQueue"/>'s.
/// </para>
/// <para>
/// A searcher with other members online in its party, asking only for lists of more than one player
/// (Party_Num 5), opens the party's role round instead (<see cref="DungeonFinderPartyRound"/>): it gets its
/// 2804, every other online member the 2822 that shows the client's role check (window 46), and everyone the
/// round is seen from a member's side only; see
/// DungeonFinderPartyRound for what it shows. A list for one player (Party_Num 1) cannot take a party, so a
/// request naming one keeps the solo path above. Only the searcher's level is tested (the 2804 17 above); the behaviour with a member outside the list's levels is not defined. The round this opens also gets its
/// timeout here: window 46 closes itself on the client after 40 s and sends nothing, so a member that lets it
/// run out would leave the round, and the party's next 1555, stuck. <see cref="DungeonFinderPartyRound.SelectRoleTimeout"/>
/// after the open, a round still open is ended for everyone (<see cref="DungeonFinderPartyRound.Expire"/>). The party
/// path lives in <see cref="DungeonFinderPartyRoundStarter"/>, which 1556 shares.
/// </para>
/// </remarks>
public class PartyMatchingFindDungeonHandler : ZoneRmiHandlerBase<PartyMatchingFindDungeonHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingFindDungeon;
    public override string Name => "PartyMatchingFindDungeon";

    public PartyMatchingFindDungeonHandler(IServiceScopeFactory s, ILogger<PartyMatchingFindDungeonHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!DungeonFinderPackets.TryParseFindDungeon1555(payload.Span, out var roleFlags, out var listIds))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyMatchingFindDungeon ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        roleFlags &= DungeonFinderPackets.RoleTank | DungeonFinderPackets.RoleDealer | DungeonFinderPackets.RoleHealer;
        if (roleFlags == 0)
        {
            // No role selected: nothing to queue for, and no answer is defined for it.
            Logger.LogDebug("Session {Id}: ReqPartyMatchingFindDungeon with no role", session.SessionId);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();
        var rows = new List<FindDungeonList>(listIds.Length);
        foreach (var id in listIds.Distinct())
        {
            var row = data.FindDungeonList.FirstOrDefault(r => r.List_ID == id);
            if (row is null)
            {
                // The client only offers rows of its own Find_Dungeon_List table.
                Logger.LogInformation("{Name} searched unknown dungeon finder list {List}", player.Name, id);
                return;
            }
            rows.Add(row);
        }

        if (rows.Find(r => player.Level < r.Min_Level || player.Level > r.Max_Level) is { } outOfRange)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeon,
                DungeonFinderPackets.BuildFindDungeonAnswer2804(DungeonFinderPackets.ResultLevel));
            Logger.LogInformation("{Name} (level {Level}) refused dungeon finder list {List} (levels {Min}-{Max})",
                player.Name, player.Level, outOfRange.List_ID, outOfRange.Min_Level, outOfRange.Max_Level);
            return;
        }

        var queue = DungeonFinderQueue.Shared;
        await queue.PruneAndPushAsync(PlayerManager, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);

        if (await DungeonFinderPartyRoundStarter.TryOpenAsync(session, player, roleFlags, rows, data, queue, services,
                PlayerManager, Broadcast, Logger, ZoneS2COpcodes.AnsPartyMatchingFindDungeon))
            return;

        var outcome = queue.Join(player, roleFlags, rows);
        if (outcome.AlreadyQueued)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeon,
                DungeonFinderPackets.BuildFindDungeonAnswer2804(DungeonFinderPackets.ResultAlreadyRegistered));
            return;
        }

        // Recorded before anything goes out: the client's 1559 answer follows the 2808 within milliseconds.
        if (outcome.Match is { } match)
            DungeonFinderPendingStore.SetPending(player, new DungeonFinderPendingStore.PendingMatch(match.ListId, match.Role, match.Group));

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeon,
            DungeonFinderPackets.BuildFindDungeonAnswer2804(DungeonFinderPackets.ResultAccepted));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo,
            DungeonFinderPackets.BuildInProgressInfo2806(outcome.ShownListId, outcome.Shown, outcome.OwnRole));

        if (outcome.Match is { } formed)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPartyMatchingFindDungeonStartJoinMessage,
                DungeonFinderPackets.BuildStartJoinMessage2808(formed.ListId, formed.Role));
            Logger.LogInformation("{Name} matched dungeon finder list {List} as role {Role}", player.Name, formed.ListId, formed.Role);
            return;
        }

        // Everyone already searching these lists sees the new member.
        await queue.PushStateAsync(outcome.Entry!.ListIds, player, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);
        Logger.LogInformation("{Name} queued for dungeon finder lists {Lists} with roles {Roles}",
            player.Name, string.Join(",", outcome.Entry.ListIds), roleFlags);
    }

    private async Task SendToSessionAsync(uint sessionId, byte[] body)
    {
        if (Broadcast.GetSession(sessionId) is { } target)
            await target.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo, body);
    }
}
