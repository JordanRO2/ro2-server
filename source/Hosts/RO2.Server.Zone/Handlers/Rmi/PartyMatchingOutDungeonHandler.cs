using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1570 ReqPartyMatching_OutDungeon: the "Exit Dungeon" sub-button on a dungeon map. Answered with 2832
/// AnsPartyMatching_OutDungeon [u16 0] and a warp to the dungeon's own return point in Map_List.
/// </summary>
/// <remarks>
/// <para>
/// Where the client sends it. <c>UISubBtnWnd_HandleButtonCommand</c>, case 15: while flying it shows
/// 10514 "Unable to enter dungeon in Flying mode."; otherwise it opens a confirm dialog titled
/// 10363 minus the AreaInfoMgr flag (set when the current map is a dungeon map),
/// i.e. 10362 "Exit Dungeon" on a flagged map and 10363 "Enter Dungeon" elsewhere. Its OK callback
/// callback sends 1570 with an empty body on a flagged map, and 1571 ReJoinDungeon elsewhere.
/// The exit/enter flag is set on every map change from the new map's row in the map table.
/// That is Map_List.Map_Type: 1 marks the
/// 64 dungeon maps and 4 the two WoE fortresses. So a 1570 comes only from a Map_Type-1 map.
/// </para>
/// <para>
/// The answer. 2832 is NONE family, one u16 (read by <c>ReadWordFromBuffer2</c>). The client's handler only logs it
/// ('%s - Error : %d', log level 4); no window waits on it and no value is shown. 0 is sent, the neutral
/// value, on the move that goes through. On any other map the client cannot have sent 1570 (it sends 1571
/// there), no failure code exists, and the consumer would only log it, so nothing is answered.
/// </para>
/// <para>
/// The destination. Every Map_Type-1 row of data/Map_List.json names its own way out (Map_ID + x/y/z, all on
/// maps the table holds), for example 410104 "EP1 - Culvert" -> 1100 (725, 796, 55.5). That is
/// the first rule of <see cref="TeleportSavePointHandler.ResolveSavePoint"/>, so it is reused rather than
/// defined a second time. The destination retail uses for a 1570 is unknown,
/// so the Map_List return point is the table's own "way out of this map".
/// </para>
/// <para>
/// The dungeon finder's run (<see cref="DungeonFinderPendingStore"/>) is left as it is: 1571 ReJoinDungeon
/// is the button the player gets back on the field, and the run is what it would rejoin.
/// </para>
/// </remarks>
public class PartyMatchingOutDungeonHandler : ZoneRmiHandlerBase<PartyMatchingOutDungeonHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatching_OutDungeon;
    public override string Name => "PartyMatching_OutDungeon";

 /// <summary>Map_List.Map_Type of a dungeon map, the value the client's exit/enter flag tests for 1.</summary>
    public const byte DungeonMapType = 1;

    public PartyMatchingOutDungeonHandler(IServiceScopeFactory s, ILogger<PartyMatchingOutDungeonHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var data = services.GetRequiredService<GameDataStore>();
        var current = data.GetMap(player.MapId);
        if (current is not { Map_Type: DungeonMapType, Map_ID: not 0 } || data.GetMap(current.Map_ID) is null)
        {
            Logger.LogInformation("{Name} asked to exit a dungeon from map {Map} (Map_Type {Type}), which is not a dungeon map with a return point; not answered",
                player.Name, player.MapId, current?.Map_Type);
            return;
        }

        
        var (mapId, x, y, z, source) = TeleportSavePointHandler.ResolveSavePoint(player, data);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatching_OutDungeon, BuildAnsOutDungeon2832());

        var ok = await WarpService.WarpAsync(session, player, mapId, x, y, z, services);
        Logger.LogInformation("{Name} exits dungeon map {From} -> map {Map} ({X}, {Y}, {Z}) from {Source}: warped={Ok}",
            player.Name, current.Map_Index, mapId, x, y, z, source, ok);
    }

 /// <summary>
 /// 2832 AnsPartyMatching_OutDungeon: NONE family, <c>[u16 0]</c>. The client's handler only logs the value.
 /// </summary>
    internal static byte[] BuildAnsOutDungeon2832() => [0, 0];
}
