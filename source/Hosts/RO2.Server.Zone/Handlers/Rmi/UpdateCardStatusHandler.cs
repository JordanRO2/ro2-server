using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1441 ReqUpdateCardStatus: reports a status change for a set of cards.
/// Request: <c>VarBlob32</c> holding a u16 count followed by records of (u32 cardId, u16, u8).
/// Answered with AnsUpdateCardStatus (2589), which carries no payload.
/// </summary>
public class UpdateCardStatusHandler : ZoneRmiHandlerBase<UpdateCardStatusHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUpdateCardStatus;
    public override string Name => "UpdateCardStatus";
    public UpdateCardStatusHandler(IServiceScopeFactory s, ILogger<UpdateCardStatusHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int RecordSize = 7;   

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        int count = 0;
        if (span.Length >= 6)
        {
            uint innerLen = BinaryPrimitives.ReadUInt32LittleEndian(span);
            count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
            
            int fit = (int)Math.Min(innerLen, (uint)(span.Length - 4));
            count = Math.Min(count, Math.Max(0, (fit - 2) / RecordSize));
        }
        var ids = new List<uint>(count);
        for (int i = 0; i < count; i++)
            ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6 + i * RecordSize)));
        Logger.LogDebug("{Name} updates card status for {Count} card(s): {Ids}", player.Name, count, string.Join(",", ids));

        
        
        
        
        
        
        
        var body = CardStatusPackets.BuildUpdateCardStatus2589(Array.Empty<CardStatusRecord>());
        await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateCardStatus, writer => writer.WriteBytes(body), ct);
    }
}
