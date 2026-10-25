using RO2.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1559 ReqPartyMatchingFindDungeonJoinSelectBtn: join the dungeon a 2808 offered. Answered with 2824
/// NfyDungeonFind_InProgress_JoinMsg and the channel hand-off to the list's dungeon, or, when the entry count is used
/// up, with a lone 2556 19015 and a 2810 [03].
/// </summary>
/// <remarks>
/// <para>
/// Body <c>[u8 choice]</c>, always 01. For a
/// Category-5 list the client sends it on its own: the 2808 handler calls the 1559 builder with 1 when the list
/// row's Category_ID is 5.
/// </para>
/// <para>
/// THE MOVE IS A CHANNEL HAND-OFF: 1559 [01] -> 2824 <c>15000000 00 01 00</c> -> the mover's
/// view-leave 2507 / 2513 (2505) -> the Warp_Handling removals 2498 / 2315 / 2497 / 2493 / 2494 -> 2938 <c>0100</c> ->
/// self 2505 -> 2556 result 0 with the destination map -> the
/// reconnect and a 2310 on that map. None of them carries a 2598. List 21 is Dungeon_ID 63 = DungeonInfo 63, whose
/// Position_ID 406 is Position_Info (72, 129, 36) on Area_ID 410104. So the departure is DungeonLevelSelectHandler's
/// (1521): <see cref="WarpDeparture.DropWarpEffectsAsync"/>, <see cref="RidingService.DropAsync"/>, then
/// <see cref="DungeonChannelHandOff.EnterAsync"/> (without the 1531-only 2598). The arrival is Position_Info[
/// Position_ID] on its own Area_ID (Map_Index1 differs from it in some DungeonInfo rows).
/// </para>
/// <para>
/// THE ENTRY COUNT. Lists 21 / 22 / 23 (Category 5, Party_Num 1) enter DungeonIDs 63 / 79 / 81, all Enter_Count 3, on
/// maps 410104 / 412104 / 421104, which are Map_List.Map_Type 1 (<see cref="DungeonEntryGate.InstanceMapType"/>).
/// A join is refused once the entry count is used up: a lone 2556 <c>1e212e00 474a</c> + zeros (result
/// 19015, string_ui 15801 "The max. entered number exceeded.") and no 2824, no 2938. Joins with fewer than 3 entries
/// used all get the 2824. So the gate is 1531's Enter_Count
/// check, applied to every join: a finder join always opens a new instance and always counts
/// (<see cref="DungeonInstanceRegistry.EnterFinder"/>; the registry's remarks hold the details). An accepted join is counted after
/// the hand-off, through <see cref="DungeonLevelSelectHandler.CountEntryAsync"/>, exactly as 1521 / 1531 count theirs,
/// and a later 1534 resets its instance with 2771 [DungeonID] unless
/// its boss died more than <see cref="DungeonInstanceRegistry.ClearedInstanceLifetime"/> before (DungeonFinderReward
/// ends the run, which starts that clock).
/// </para>
/// <para>
/// 2810 NfyPartyMatchingFindDungeonJoin_Fail <c>03</c> follows the refusal. Each refused join is paired with one 2810 (<c>03</c>)
/// and the refusal is known at once, so the 2810 goes out right after
/// the 2556. Its consumer only
/// hides the finder windows 42 (join prompt), 41 and 43 and the minimap element and clears the finding state, and
/// shows nothing for reason 3; the 19015 message comes from the 2556, not from a
/// finder window. Reason 2, the only one with a message (10610), is the group re-queue case and is never sent, since
/// this server forms solo matches only. The same 2810 answers the guards: a list with no resolvable arrival point
/// (none with today's tables: every list resolves to a Map_List map) and a join that arrives while this connection has
/// no loaded map (a hand-off already went out, or a map load is in progress), which cannot be handed off.
/// </para>
/// <para>
/// A 1559 with no match waiting (none formed, or one already answered) gets nothing: two quick 1555s
/// get two full 2804/2806/2808 answers and two 1559s, but only a single 2824. 2809 AnsPartyMatchingFindDungeonJoinSelectBtn is never
/// sent: the client has no handler for it. A choice 0
/// (decline) drops the match.
/// </para>
/// <para>
/// The run is recorded (DungeonFinderPendingStore.SetRun, per character, since the hand-off rebuilds the
/// PlayerState) before the hand-off, so the list's Last_Boss_ID pays the 2816 (DungeonFinderReward).
/// </para>
/// </remarks>
public class PartyMatchingFindDungeonJoinSelectBtnHandler : ZoneRmiHandlerBase<PartyMatchingFindDungeonJoinSelectBtnHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingFindDungeonJoinSelectBtn;
    public override string Name => "PartyMatchingFindDungeonJoinSelectBtn";

    /// <summary>1559 body choice that declines the offered match (the client always sends 1 to join).</summary>
    private const byte ChoiceDecline = 0;

    /// <summary>DungeonInfo.Enter_Count value meaning the dungeon has no entry limit.</summary>
    private const int NoEntryLimit = 0;

    /// <summary><see cref="DungeonEntryGate.CheckEnterCount"/> result meaning the entry is allowed.</summary>
    private const ushort EntryAllowed = 0;

    public PartyMatchingFindDungeonJoinSelectBtnHandler(IServiceScopeFactory s, ILogger<PartyMatchingFindDungeonJoinSelectBtnHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var choice = payload.Length >= 1 ? payload.Span[0] : (byte)0;
        var match = DungeonFinderPendingStore.TakePending(player);
        if (match is null)
        {
            Logger.LogDebug("Session {Id}: PartyMatchingFindDungeonJoinSelectBtn {Choice} with no dungeon finder match waiting",
                session.SessionId, choice);
            return;
        }
        if (choice == ChoiceDecline)
        {
            Logger.LogInformation("{Name} declined dungeon finder list {List}", player.Name, match.ListId);
            return;
        }

        var data = services.GetRequiredService<GameDataStore>();
        var list = data.FindDungeonList.FirstOrDefault(r => r.List_ID == match.ListId);
        var dungeon = list is null ? null : data.DungeonInfo.FirstOrDefault(d => d.DungeonID == list.Dungeon_ID);
        if (dungeon is null
            || !data.Positions.TryGetValue(dungeon.Position_ID, out var pos)
            || data.GetMap(pos.Area_ID) is not { } map)
        {
            
            Logger.LogWarning("{Name} joined dungeon finder list {List}, which has no resolvable arrival point",
                player.Name, match.ListId);
            await SendJoinFailAsync(session);
            return;
        }
        if (!player.MapLoaded)
        {
            
            Logger.LogInformation("{Name} joined dungeon finder list {List} with no loaded map: not handed off, 2810",
                player.Name, match.ListId);
            await SendJoinFailAsync(session);
            return;
        }

        
        var isInstance = map.Map_Type == DungeonEntryGate.InstanceMapType;
        var owner = DungeonInstanceOwner.For(
            services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId), player.CharacterId);
        var counts = isInstance && dungeon.Enter_Count != NoEntryLimit
            ? services.GetRequiredService<IDungeonEntryCountRepository>()
            : null;
        if (counts is not null)
        {
            
            var used = await counts.GetUsedAsync(player.CharacterId, dungeon.DungeonID, DateTime.UtcNow, ct);
            var refusal = DungeonEntryGate.CheckEnterCount(dungeon, wouldCount: true, used);
            if (refusal != EntryAllowed)
            {
                
                
                await DungeonEntryGate.RefuseAsync(session, session.AccountId ?? player.AccountId, refusal);
                await SendJoinFailAsync(session);
                Logger.LogInformation("{Name} joined dungeon finder list {List} (DungeonID {Dun}) with {Used}/{Limit} entries used: refused {Code}, 2810",
                    player.Name, match.ListId, dungeon.DungeonID, used, dungeon.Enter_Count, refusal);
                return;
            }
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyDungeonFind_InProgress_JoinMsg,
            DungeonFinderPackets.BuildInProgressJoinMsg2824(match.ListId, match.Group));

        
        DungeonInstanceEntry? entry = isInstance
            ? DungeonInstanceRegistry.Shared.EnterFinder(owner, dungeon.DungeonID, pos.Area_ID, player.CharacterId)
            : null;
        DungeonFinderPendingStore.SetRun(player,
            new DungeonFinderPendingStore.FinderRun(match.ListId, pos.Area_ID, entry?.InstanceId));

        
        await WarpDeparture.DropWarpEffectsAsync(player, data, services.GetRequiredService<EffectService>());
        await RidingService.DropAsync(session, player, services);
        Logger.LogInformation("{Name} joins dungeon finder list {List} (DungeonID {Dun}) -> Position_Info {Pos} map {Map}{Instance}",
            player.Name, match.ListId, dungeon.DungeonID, dungeon.Position_ID, pos.Area_ID,
            entry is { } e ? $": {owner} instance {e.InstanceId} opened" : "");
        await DungeonChannelHandOff.EnterAsync(session, player, pos, services, Logger, ct);

        if (entry is { CountsForCharacter: true } && counts is not null)
            await DungeonLevelSelectHandler.CountEntryAsync(counts, player, dungeon, Logger, ct);
    }

 /// <summary>
 /// 2810 NfyPartyMatchingFindDungeonJoin_Fail [03], the answer to a refused finder join (see the class
 /// remarks). Its consumer closes the join prompt, the finding state and window 43.
 /// </summary>
    private static Task SendJoinFailAsync(ProudSession session)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPartyMatchingFindDungeonJoin_Fail,
            DungeonFinderPackets.BuildJoinFail2810(DungeonFinderPackets.JoinFailMoveFailed));
}
