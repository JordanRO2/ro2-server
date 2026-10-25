using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1556 ReqPartyMatchingFindDungeonRandom: a dungeon finder search for a random dungeon of one
/// Find_Dungeon_Category (2 Normal, 3 Hard). Answered with 2805 and, when accepted, the 2806 queue state, the
/// way <see cref="PartyMatchingFindDungeonHandler"/> answers a 1555.
/// </summary>
/// <remarks>
/// <para>
/// Body <c>[u8 role flags][u8 Category_ID]</c> (<see cref="DungeonFinderPackets.TryParseFindDungeonRandom1556"/>),
/// from the client's builder alone.
/// The client itself refuses a search with no role (10155) and, in a party, one from anyone but the leader
/// (10347 "Only a party leader can search dungeon.").
/// </para>
/// <para>
/// The search covers every Find_Dungeon_List row of that category whose Min_Level..Max_Level holds the
/// searcher's level, in table order (Category 2 = lists 1, 3, .., 19 with level bands; Category 3 = lists 2, 4,
/// .., 20, all level 50). None in range is answered 17, string 2741 "Your level incorrect to use.", the code
/// retail sent for a 1555 out of its list's level band. A searcher still queued is answered
/// 9 (10607 "Already registered in the Find Dungeon list."). Otherwise 2805 0 and the 2806 of the queue,
/// naming the first matching row; every row of both categories has the same quotas (1 tank, 1 healer,
/// 3 dealers, Party_Num 5), so the slot icons do not depend on that choice. Retail's 2806 for a random search
/// is not observed. Party_Num-5 rows are queued and counted but never matched here, which is also what
/// retail's two list-4 searches show (2804 + 2806 only); see <see cref="DungeonFinderQueue"/>.
/// 1558 cancels the search like any other.
/// </para>
/// <para>
/// IN A PARTY (the client lets only its leader send 1556), a searcher with other members online
/// opens the party's role round instead of queueing alone, exactly as <see cref="PartyMatchingFindDungeonHandler"/>
/// does for these same Party_Num-5 rows (<see cref="DungeonFinderPartyRoundStarter"/>): the searcher gets 2805 0,
/// every other online member the 2822 role check (window 46), everyone the round's 2806 (List_ID 0, the shape of
/// retail's only observed rounds), and the round expires after
/// <see cref="DungeonFinderPartyRound.SelectRoleTimeout"/>. A solo search still queued, or a round already open, is
/// answered 2805 9. The level test stays the searcher's own, as for 1555. This party path comes from the client's
/// builder/reader and the 1555 model alone.
/// </para>
/// </remarks>
public class PartyMatchingFindDungeonRandomHandler : ZoneRmiHandlerBase<PartyMatchingFindDungeonRandomHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingFindDungeonRandom;
    public override string Name => "PartyMatchingFindDungeonRandom";

    public PartyMatchingFindDungeonRandomHandler(IServiceScopeFactory s, ILogger<PartyMatchingFindDungeonRandomHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!DungeonFinderPackets.TryParseFindDungeonRandom1556(payload.Span, out var roleFlags, out var categoryId))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyMatchingFindDungeonRandom ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        roleFlags &= DungeonFinderPackets.RoleTank | DungeonFinderPackets.RoleDealer | DungeonFinderPackets.RoleHealer;
        if (roleFlags == 0)
        {
            
            Logger.LogDebug("Session {Id}: ReqPartyMatchingFindDungeonRandom with no role", session.SessionId);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();
        var inCategory = data.FindDungeonList.Where(r => r.Category_ID == categoryId).ToList();
        if (inCategory.Count == 0)
        {
            
            Logger.LogInformation("{Name} searched random dungeon category {Cat}, which has no Find_Dungeon_List row",
                player.Name, categoryId);
            return;
        }

        var rows = inCategory.FindAll(r => player.Level >= r.Min_Level && player.Level <= r.Max_Level);
        if (rows.Count == 0)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom,
                DungeonFinderPackets.BuildFindDungeonRandomAnswer2805(DungeonFinderPackets.ResultLevel));
            Logger.LogInformation("{Name} (level {Level}) refused random dungeon category {Cat}: no list in level range",
                player.Name, player.Level, categoryId);
            return;
        }

        var queue = DungeonFinderQueue.Shared;
        await queue.PruneAndPushAsync(PlayerManager, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);

        
        
        if (await DungeonFinderPartyRoundStarter.TryOpenAsync(session, player, roleFlags, rows, data, queue, services,
                PlayerManager, Broadcast, Logger, ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom))
            return;

        var outcome = queue.Join(player, roleFlags, rows);
        if (outcome.AlreadyQueued)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom,
                DungeonFinderPackets.BuildFindDungeonRandomAnswer2805(DungeonFinderPackets.ResultAlreadyRegistered));
            return;
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeonRandom,
            DungeonFinderPackets.BuildFindDungeonRandomAnswer2805(DungeonFinderPackets.ResultAccepted));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo,
            DungeonFinderPackets.BuildInProgressInfo2806(outcome.ShownListId, outcome.Shown, outcome.OwnRole));

        if (outcome.Match is { } formed)
        {
            
            
            DungeonFinderPendingStore.SetPending(player, new DungeonFinderPendingStore.PendingMatch(formed.ListId, formed.Role, formed.Group));
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPartyMatchingFindDungeonStartJoinMessage,
                DungeonFinderPackets.BuildStartJoinMessage2808(formed.ListId, formed.Role));
            Logger.LogInformation("{Name} matched dungeon finder list {List} as role {Role}", player.Name, formed.ListId, formed.Role);
            return;
        }

        
        await queue.PushStateAsync(outcome.Entry!.ListIds, player, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);
        Logger.LogInformation("{Name} queued for random dungeon category {Cat}: lists {Lists} with roles {Roles}",
            player.Name, categoryId, string.Join(",", outcome.Entry.ListIds), roleFlags);
    }

    private async Task SendToSessionAsync(uint sessionId, byte[] body)
    {
        if (Broadcast.GetSession(sessionId) is { } target)
            await target.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo, body);
    }
}
