using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ZoneClientReadyHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1590 (ZoneClientReady), sent by the client after character creation and other state
/// transitions. The message has no payload. It is answered with AnsReservedPCList (RMI 2862) reporting no
/// reserved characters.
/// </summary>
public class ZoneClientReadyHandler : NetworkZoneRmiBase
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqReservedPCList;
    public override string Name => "ZoneClientReady";

    /// <summary>
    /// Body of the 2862 AnsReservedPCList answer (3 B): a u8 result 0, then the reserved-PC vector as a
    /// compact-length count (size prefix 0x01 = one count byte, count 0), i.e. an empty list.
    /// </summary>
    private static readonly byte[] EmptyReservedPCListBody = BuildAnsReservedPCList2862(result: 0);

    /// <summary>Builds the 2862 body: <c>[u8 result][compact count][reserved-PC entries]</c>, here with no entries.</summary>
    private static byte[] BuildAnsReservedPCList2862(byte result)
    {
        using var pw = new PacketWriter();
        pw.WriteByte(result);        // +0 result
        pw.WriteCompactLength(0);    // +1 compact count of reserved PCs (01 00)
        return pw.ToArray();
    }

    public ZoneClientReadyHandler(IServiceScopeFactory scopeFactory, ILogger<ZoneClientReadyHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {SessionId}: ZoneClientReady (RMI 1590) received ({Size} bytes)",
            session.SessionId, payload.Length);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsReservedPCList, EmptyReservedPCListBody);
    }
}
