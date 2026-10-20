using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1606 ReqCharNameList (empty body). Answered to the requester alone with S2C 2892 AnsCharNameList.
/// </summary>
/// <remarks>
/// The reply is a compact vector of wide strings: a compact element count, then for each element a width tag
/// and a compact character count. An empty list is encoded as <c>01 00</c>. The client discards the names it
/// receives, so the list is sent empty.
/// </remarks>
public class CharNameListHandler : ZoneRmiHandlerBase<CharNameListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCharNameList;
    public override string Name => "CharNameList";

    public CharNameListHandler(IServiceScopeFactory s, ILogger<CharNameListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>The 2892 body for an empty name list: compact length 0, encoded as <c>01 00</c>.</summary>
    internal static byte[] BuildEmptyAnsCharNameList()
    {
        using var w = new PacketWriter(2);
        w.WriteCompactLength(0);
        return w.ToArray();
    }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.AnsCharNameList, BuildEmptyAnsCharNameList(), ct);
}
