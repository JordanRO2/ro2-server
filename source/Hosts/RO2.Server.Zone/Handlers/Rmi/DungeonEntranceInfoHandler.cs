using RO2.Application.Interfaces;
using System.Buffers.Binary;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1699 ReqDungeonEnteranceInfo, which asks how many entries the character has used for a dungeon.</summary>
/// <remarks>
/// The request body is a u32 DungeonID. Only dungeons with an entry limit are answered, with AnsDungeonEnteranceInfo
/// (3035) carrying the DungeonID, the entries used in the current window, and the entry limit. Entry counts reset
/// daily at 10:00 UTC.
/// </remarks>
public class DungeonEntranceInfoHandler : ZoneRmiHandlerBase<DungeonEntranceInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDungeonEnteranceInfo;
    public override string Name => "DungeonEntranceInfo";
    public DungeonEntranceInfoHandler(IServiceScopeFactory s, ILogger<DungeonEntranceInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        var dungeonId = new PacketReader(payload.Span).ReadUInt32();

        var data = services.GetRequiredService<GameDataStore>();
        var dungeon = data.DungeonInfo.FirstOrDefault(d => d.DungeonID == dungeonId);

        
        if (dungeon == null || dungeon.Enter_Count == 0)
        {
            Logger.LogDebug("{Name} asked entrance info of dungeon {Dun}: no entry limit, no 3035", player.Name, dungeonId);
            return;
        }

        var used = await services.GetRequiredService<IDungeonEntryCountRepository>()
            .GetUsedAsync(player.CharacterId, dungeonId, DateTime.UtcNow, ct);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsDungeonEnteranceInfo,
            Build3035(dungeonId, (ushort)Math.Min(used, ushort.MaxValue), dungeon.Enter_Count));
    }

 /// <summary>Builds the 8-byte AnsDungeonEnteranceInfo body.</summary>
 /// <param name="dungeonId">The DungeonID to echo.</param>
 /// <param name="used">Entries used in the current reset window.</param>
 /// <param name="limit">The dungeon's entry limit.</param>
    internal static byte[] Build3035(uint dungeonId, ushort used, ushort limit)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeonId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), used);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), limit);
        return body;
    }
}
