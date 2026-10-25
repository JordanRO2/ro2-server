using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1497 ReqPartyTypeChange - Change party type (Normal/Raid)</summary>
public class PartyTypeChangeHandler : ZoneRmiHandlerBase<PartyTypeChangeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyTypeChange;
    public override string Name => "PartyTypeChange";

    public PartyTypeChangeHandler(IServiceScopeFactory s, ILogger<PartyTypeChangeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 1) return;
        var reader = new PacketReader(payload.Span);
        var partyType = reader.ReadByte();

        
        
        var onDungeonMap = IsDungeonMap(services.GetRequiredService<GameDataStore>().GetMap(player.MapId));

        var partyService = services.GetRequiredService<PartyService>();
        await partyService.ChangeTypeAsync(session.SessionId, partyType, onDungeonMap);
    }

 /// <summary>
 /// Whether <paramref name="map"/> is a dungeon map, i.e. its Map_Type equals <see cref="PartyMatchingOutDungeonHandler.DungeonMapType"/>.
 /// A null map is not a dungeon.
 /// </summary>
    internal static bool IsDungeonMap(MapData? map)
        => map?.Map_Type == PartyMatchingOutDungeonHandler.DungeonMapType;
}
