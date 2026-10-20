using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1381 ReqPartyEffectList — request for the party buff/effect list.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Nothing is answered.</b> This handler used to reply 2790 NfyPartyItemListClose with a 4-byte
/// body carrying the requesting player's own handle. That packet was wrong in every respect:
/// </para>
/// <list type="bullet">
/// <item><b>Wrong length.</b> The 2790 body is 12 bytes: <c>[u32 handle][u64 0]</c>. Ours was 4.</item>
/// <item><b>Wrong handle class.</b> The u32 carries the NPC entity tag (0x13) in its low byte.
/// <c>player.EntityId</c> is a PC handle (0x11).</item>
/// <item><b>Wrong message entirely.</b> 2790 answers no request. It is the server closing the party LOOT
/// list for a corpse, a party-item notify keyed to a monster (the handle also appears at +26 of a preceding
/// <b>2362 OnPartyNotifyGetItem</b>), not an effect list.</item>
/// </list>
/// <para>
/// No other code sends 2790 either, on purpose: its consumer grants loot authority on the corpse, and
/// our auto-looted corpses have nothing to grant. The reason is recorded on
/// <see cref="ZoneS2COpcodes.NfyPartyItemListClose"/>.
/// </para>
/// <para>
/// There is no reply defined for 1381: no exchange for this opcode is known, and the client has no send
/// path that waits on an answer. The request body is a <c>VarBlobC</c> (a 2-3 byte Proud compact-length
/// prefix, not an 8-byte header), and nothing is read out of it, so it is left unparsed.
/// </para>
/// </remarks>
public class PartyEffectListHandler : ZoneRmiHandlerBase<PartyEffectListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyEffectList;
    public override string Name => "PartyEffectList";

    public PartyEffectListHandler(IServiceScopeFactory s, ILogger<PartyEffectListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name}: {Player} requested the party effect list ({Size} bytes); no derived "
            + "reply exists for 1381, so nothing is sent", Name, player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
