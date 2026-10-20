using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles a change of the party experience distribution rule (RMI 1386, ReqPartyChangeExpRule).</summary>
/// <remarks>
/// <para>
/// Blob: <c>[u16 9][u16 58148][u32 own PC handle at offset 4][u8 rule at offset 8]</c>.
/// </para>
/// <para>
/// The answer is built by <see cref="PartyStatePackets.BuildOnPartyChangeExpAck"/>. On success the client shows nothing.
/// </para>
/// </remarks>
public class PartyChangeExpRuleHandler : ZoneRmiHandlerBase<PartyChangeExpRuleHandler>
{
 /// <summary>msgTag of the 1386 request.</summary>
    private const ushort MsgTagReqPartyChangeExpRule = 58148;

 /// <summary>Blob length: the 8-byte envelope plus the rule byte.</summary>
    private const int BlobLength = 9;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyChangeExpRule;
    public override string Name => "PartyChangeExpRule";

    public PartyChangeExpRuleHandler(IServiceScopeFactory s, ILogger<PartyChangeExpRuleHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var handle, out var newRule))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyChangeExpRule ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        var partyService = services.GetRequiredService<PartyService>();
        if (!await partyService.ChangeExpRuleAsync(session.SessionId, handle, newRule))
            Logger.LogDebug("{Name}: 1386 rule {Rule} not taken (no party, or not its leader)", player.Name, newRule);
    }

 /// <summary>Reads the request blob: the sender's handle at offset 4 and the rule byte at offset 8.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out uint handle, out byte rule)
    {
        rule = 0;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyChangeExpRule, BlobLength, out handle))
        {
            handle = 0;
            return false;
        }
        rule = blob[8];
        return true;
    }
}
