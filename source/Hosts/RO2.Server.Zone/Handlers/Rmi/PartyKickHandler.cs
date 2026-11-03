using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1388 ReqPartyExpulsion - Kick member from party</summary>
public class PartyKickHandler : ZoneRmiHandlerBase<PartyKickHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyExpulsion;
    public override string Name => "PartyKick";

    public PartyKickHandler(IServiceScopeFactory s, ILogger<PartyKickHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (!PartyAnswerPackets.TryGetCompactBlob(payload.Span, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyExpulsion, 16, out var handle))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyExpulsion ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var targetGid = BinaryPrimitives.ReadUInt64LittleEndian(blob[8..]);
        if (targetGid == 0) return;

        await services.GetRequiredService<PartyService>().KickAsync(session.SessionId, handle, targetGid);
    }
}
