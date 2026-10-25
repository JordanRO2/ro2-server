using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1588 ReqGiveItemRemainTime (empty request body) - query remaining time of timed item</summary>
public class GiveItemRemainTimeHandler : ZoneRmiHandlerBase<GiveItemRemainTimeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGiveItemRemainTime;
    public override string Name => "GiveItemRemainTime";
    public GiveItemRemainTimeHandler(IServiceScopeFactory s, ILogger<GiveItemRemainTimeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Length in bytes of the blob inside the 2857 body: u32@0, u32@4, u16@8, u32@10, u16@14.</summary>
    private const uint AnsGiveItemRemainTimeBlobLength = 16;

    /// <summary>Blob+0 (u32): the first field the client reads from the answer. The reply is the same on every request.</summary>
    private const uint RemainTimeFirstField = 1;

    /// <summary>
    /// The 2857 AnsGiveItemRemainTime body: a 20-byte, u32-length-prefixed blob (not the compact-length
    /// envelope) of <see cref="AnsGiveItemRemainTimeBlobLength"/> bytes, [u32 1][u32 0][u16 0][u32 0][u16 0].
    /// It is a constant: the request carries nothing and this server has no timed-gift state to report, so
    /// every reply is this neutral shape. The client reads the u32 at blob+0 and the field at blob+14.
    /// </summary>
    private static readonly byte[] AnsGiveItemRemainTimeBody = BuildAnsGiveItemRemainTimeBody();

    private static byte[] BuildAnsGiveItemRemainTimeBody()
    {
        using var w = new PacketWriter(24);
        w.WriteUInt32(AnsGiveItemRemainTimeBlobLength); // u32 length prefix
        w.WriteUInt32(RemainTimeFirstField);            // blob+0
        w.WriteUInt32(0);                               // blob+4
        w.WriteUInt16(0);                               // blob+8
        w.WriteUInt32(0);                               // blob+10
        w.WriteUInt16(0);                               // blob+14
        return w.ToArray();
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} queries timed item remaining time", player.Name);

        // AnsGiveItemRemainTime: the constant 20-byte body.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGiveItemRemainTime, writer => writer.WriteBytes(AnsGiveItemRemainTimeBody));
    }
}
