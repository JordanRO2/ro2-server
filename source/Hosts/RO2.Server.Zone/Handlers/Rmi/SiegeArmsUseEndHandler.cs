using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1638 ReqSiegeArmsUseEnd. Stops using a siege weapon.</summary>
public class SiegeArmsUseEndHandler : ZoneRmiHandlerBase<SiegeArmsUseEndHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSiegeArmsUseEnd;
    public override string Name => "SiegeArmsUseEnd";

    /// <summary>
    /// 2938 AnsSiegeArmsUseEnd result: this session was seated at an arm and has now left it. The client leaves
    /// arm mode on 0.
    /// </summary>
    private const ushort UseEndResultLeft = 0;

    /// <summary>
    /// 2938 AnsSiegeArmsUseEnd result: this session was not seated (the second click of a doubled request). The
    /// client ignores a nonzero value.
    /// </summary>
    private const ushort UseEndResultNotSeated = 1;

    /// <summary>2940 NfySiegeArmsUserChanged operator handle meaning the seat is vacated.</summary>
    private const uint OperatorHandleVacated = 0u;

    public SiegeArmsUseEndHandler(IServiceScopeFactory s, ILogger<SiegeArmsUseEndHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>
    /// 2938 AnsSiegeArmsUseEnd body: a plain little-endian u16 result, not a ByteArray (<c>00 00</c> left,
    /// <c>01 00</c> not seated).
    /// </summary>
    private static byte[] BuildUseEndAnswer2938(ushort result)
    {
        using var w = new PacketWriter(2);
        w.WriteUInt16(result);
        return w.ToArray();
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The C2S body carries no handle; recover the arm this session was operating and vacate it.
        var arm = SiegeService.EndOperate(session.SessionId);

        // 2938 answers the request with a plain u16: 0 when this session was seated (the client leaves arm mode),
        // nonzero when it was not (the second click of a doubled request; the client ignores it).
        if (arm is null)
        {
            Logger.LogDebug("{Name} sent 1638 while not operating a siege weapon", player.Name);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, BuildUseEndAnswer2938(UseEndResultNotSeated));
            return;
        }

        Logger.LogInformation("{Name} stops using siege weapon {Handle}", player.Name, arm.Handle);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, BuildUseEndAnswer2938(UseEndResultLeft));

        // 2940 NfySiegeArmsUserChanged [u32 armHandle][u32 operatorHandle = 0], 8 bytes, vacates the seat. The
        // operator's own connection gets it here; BroadcastToNearbyAsync skips the source session, so bystanders
        // get the broadcast.
        await session.SendRmiAsync(ZoneS2COpcodes.NfySiegeArmsUserChanged,
            w => w.WriteBytes(SiegePackets.BuildSiegeArmsUserChanged(arm.Handle, OperatorHandleVacated)));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySiegeArmsUserChanged,
            w => w.WriteBytes(SiegePackets.BuildSiegeArmsUserChanged(arm.Handle, OperatorHandleVacated)));
    }
}
