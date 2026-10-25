using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1521 AnsDungeonLevelSelect, sent when the player confirms a dungeon and difficulty at an entrance.</summary>
/// <remarks>
/// The request body is a fixed 7-byte record: a u32 DungeonGroupID, a u16 Map_Level, and a u8 flag. The request
/// resolves to the DungeonInfo row with the same group and level. Requests with no unique match are ignored.
/// The character's level, quest state, and remaining entries are checked before the move. A refused entry gets
/// only a channel-move reply carrying the refusal code. Instance dungeons enter a per-party or per-character
/// instance, and an entry is counted once per character and instance. Other maps are reached by a same-server warp
/// to the position given by the row. A repeated request sent while the map is still loading is ignored, so the
/// character moves only once.
/// </remarks>
public class DungeonLevelSelectHandler : ZoneRmiHandlerBase<DungeonLevelSelectHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsDungeonLevelSelect; 
    public override string Name => "DungeonLevelSelect";

    public DungeonLevelSelectHandler(IServiceScopeFactory s, ILogger<DungeonLevelSelectHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 7)
        {
            Logger.LogDebug("{Name} sent a {Len}-byte 1521; every retail frame is 7 bytes, ignored", player.Name, payload.Length);
            return;
        }
        if (!player.MapLoaded || player.PendingGliderDismount)
        {
            
            
            Logger.LogDebug("{Name} sent a 1521 before acking its map load, ignored", player.Name);
            return;
        }
        var reader = new PacketReader(payload.Span);
        var groupId = reader.ReadUInt32(); 
        var level = reader.ReadUInt16();   
        
        var data = services.GetRequiredService<GameDataStore>();
        var rows = data.DungeonInfo
            .Where(d => d.DungeonGroupID == groupId && d.Map_Level == level)
            .ToList();
        if (rows.Count != 1)
        {
            
            
            Logger.LogInformation("{Name} selected dungeon group {Group} level {Level}: {Count} DungeonInfo rows, no move",
                player.Name, groupId, level, rows.Count);
            return;
        }
        var row = rows[0];

        if (!data.Positions.TryGetValue(row.Position_ID, out var pos) || data.GetMap(pos.Area_ID) is null)
        {
            
            
            
            Logger.LogWarning("{Name} selected DungeonID {Dun} (group {Group} level {Level}): Position_Info {Pos} missing or its map is not in Map_List, no move",
                player.Name, row.DungeonID, groupId, level, row.Position_ID);
            return;
        }

        var map = data.GetMap(pos.Area_ID)!; 
        var party = services.GetRequiredService<PartyService>().GetPlayerParty(player.SessionId);
        
        var accountId = session.AccountId ?? player.AccountId;
        
        
        var trainingGround = DungeonEntryGate.IsEntryControlled(data, pos.Area_ID);

        if (map.Map_Type != DungeonEntryGate.InstanceMapType && !trainingGround)
        {
            
            
            
            await WarpDeparture.DepartAsync(session, player, services, Logger, ct);
            var ok = await WarpService.WarpAsync(session, player, pos.Area_ID,
                pos.Position_X, pos.Position_Y, pos.Position_Z, services);
            Logger.LogInformation("{Name} enters DungeonID {Dun} (group {Group} level {Level}) -> Position_Info {Pos} map {Map} (Map_Type {Type}) ({X:F1},{Y:F1},{Z:F1}): warped={Ok}",
                player.Name, row.DungeonID, groupId, level, row.Position_ID, pos.Area_ID, map.Map_Type,
                pos.Position_X, pos.Position_Y, pos.Position_Z, ok);
            return;
        }

        
        
        var registry = DungeonInstanceRegistry.Shared;
        var owner = DungeonInstanceOwner.For(party, player.CharacterId);
        var wouldCount = !trainingGround && registry.WouldCount(owner, row.DungeonID, player.CharacterId);
        var counts = row.Enter_Count != 0 && !trainingGround
            ? services.GetRequiredService<IDungeonEntryCountRepository>()
            : null;
        var used = counts is not null && wouldCount
            ? await counts.GetUsedAsync(player.CharacterId, row.DungeonID, DateTime.UtcNow, ct)
            : 0;
        var questDone = row.Need_Quest_ID == 0
            || (await services.GetRequiredService<IQuestRepository>().GetCompletedQuestIdsAsync(player.CharacterId))
                .Contains(row.Need_Quest_ID);
        
        
        var refusal = DungeonEntryGate.CheckEntryControl(data, pos.Area_ID, party is not null);
        if (refusal == 0)
            refusal = DungeonEntryGate.CheckInstanceEntry(row, player.Level, questDone, wouldCount, used);
        if (refusal != 0)
        {
            await DungeonEntryGate.RefuseAsync(session, accountId, refusal);
            Logger.LogInformation("{Name} (level {PcLevel}, in a party: {InParty}) selected DungeonID {Dun} (group {Group} level {Level}; min {Min} max {Max}, quest {Quest} done={Done}, entries {Used}/{Limit}): refused {Code}",
                player.Name, player.Level, party is not null, row.DungeonID, groupId, level, row.Map_Min_Level, row.Map_Max_Level,
                row.Need_Quest_ID, questDone, used, row.Enter_Count, refusal);
            return;
        }

        
        
        
        
        
        
        
        DungeonInstanceEntry? entry = trainingGround
            ? null
            : registry.Enter(owner, row.DungeonID, pos.Area_ID, player.CharacterId);
        var announce = entry is { } entered
            ? entered.IsNewInstance
            : DungeonEntryGate.OpensTrainingRoom(PlayerManager.GetPlayersOnMap(pos.Area_ID), player.SessionId);
        if (announce)
            await DungeonLevelSelectInfoNotify.SendToPartyAsync(player, groupId, level, services, Logger);

        
        
        await WarpDeparture.DropWarpEffectsAsync(player, data, services.GetRequiredService<EffectService>());
        await RidingService.DropAsync(session, player, services);

        if (entry is { } e)
            Logger.LogInformation("{Name} enters DungeonID {Dun} (group {Group} level {Level}) -> Position_Info {Pos} map {Map}: {Owner} instance {Instance} ({Kind})",
                player.Name, row.DungeonID, groupId, level, row.Position_ID, pos.Area_ID, owner, e.InstanceId,
                e.IsNewInstance ? "opened" : "re-entered");
        else
            Logger.LogInformation("{Name} enters the Training Ground, DungeonID {Dun} (group {Group} level {Level}) -> Position_Info {Pos} map {Map}: {Kind} its room",
                player.Name, row.DungeonID, groupId, level, row.Position_ID, pos.Area_ID, announce ? "opens" : "joins");
        await DungeonChannelHandOff.EnterAsync(session, player, pos, services, Logger, ct);

        if (entry is { CountsForCharacter: true } && counts is not null)
            await CountEntryAsync(counts, player, row, Logger, ct);
    }

 /// <summary>Records an entry that has already been handed off. A storage failure is logged, not thrown.</summary>
    internal static async Task CountEntryAsync(IDungeonEntryCountRepository counts, PlayerState player,
        RO2.GameData.Tables.DungeonInfo row, ILogger logger, CancellationToken ct)
    {
        try
        {
            var now = await counts.RecordEntryAsync(player.CharacterId, row.DungeonID, DateTime.UtcNow, ct);
            logger.LogInformation("{Name}: DungeonID {Dun} entries {Used}/{Limit} since the last reset",
                player.Name, row.DungeonID, now, row.Enter_Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: could not count the entry into DungeonID {Dun}", player.Name, row.DungeonID);
        }
    }
}
