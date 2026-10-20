using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1383 ReqPartyJoin - Accept party invite</summary>
public class PartyJoinHandler : ZoneRmiHandlerBase<PartyJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyJoin;
    public override string Name => "PartyJoin";

    public PartyJoinHandler(IServiceScopeFactory s, ILogger<PartyJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        var span = payload.Span;
        if (span.Length < 62) return; 
        var responderHandle = BinaryPrimitives.ReadUInt32LittleEndian(span[6..]);
        var answer = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var inviterGid = BinaryPrimitives.ReadUInt64LittleEndian(span[12..]);
        var reader = new PacketReader(span);
        reader.Skip(20);
        var inviterName = reader.ReadWideStringFixed(21).TrimEnd('\0');

        
        
        
        
        var partyService = services.GetRequiredService<PartyService>();
        await partyService.AnswerInviteAsync(player, responderHandle, answer, inviterGid, inviterName);
    }
}
