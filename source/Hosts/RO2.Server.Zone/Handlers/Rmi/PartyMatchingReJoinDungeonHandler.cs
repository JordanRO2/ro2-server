using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1571 ReqPartyMatching_ReJoinDungeon: the "Enter Dungeon" sub-button off a dungeon map. Answered with
/// 2833 AnsPartyMatching_ReJoinDungeon [u16 0].
/// </summary>
/// <remarks>
/// <para>
/// The 2833 layout comes from the client's reader alone. 2833 is NONE family, one u16. The client's handler only logs it (LogSystem level 4) and
/// returns 1: no window and no branch depend on the value. 0 is the neutral value, exactly as its 1570 twin
/// 2832 (<see cref="PartyMatchingOutDungeonHandler.BuildAnsOutDungeon2832"/>). The old body was one byte, a
/// short read of that u16.
/// </para>
/// <para>
/// The client sends 1571 on a non-dungeon map, behind sub-button 15, which
/// only roster header blob+33 enables; that byte is 0 in every retail roster and in ours, so the path is
/// unreachable today. Rejoining the dungeon finder's run is not implemented here.
/// </para>
/// </remarks>
public class PartyMatchingReJoinDungeonHandler : ZoneRmiHandlerBase<PartyMatchingReJoinDungeonHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatching_ReJoinDungeon;
    public override string Name => "PartyMatching_ReJoinDungeon";

    public PartyMatchingReJoinDungeonHandler(IServiceScopeFactory s, ILogger<PartyMatchingReJoinDungeonHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: PartyMatching_ReJoinDungeon", session.SessionId);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatching_ReJoinDungeon, BuildAnsReJoinDungeon2833());
    }

 /// <summary>
 /// 2833 AnsPartyMatching_ReJoinDungeon: NONE family, <c>[u16 0]</c>. The client's handler only logs the value.
 /// </summary>
    internal static byte[] BuildAnsReJoinDungeon2833() => [0, 0];
}
