using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1411 (ReqFunctionObjectControlCancel): the player stops controlling the function object or NPC
/// that a 1410 request put under its control, such as gathering from or talking to it.
/// </summary>
/// <remarks>
/// <para>
/// Body: <c>[01 0c][u16 innerSize=12][u16 msgTag=58356][u32 pcHandle][u32 targetHandle]</c>. The target is the
/// handle carried by the starting 1410.
/// </para>
/// <para>
/// Replies with two compact messages, both 18 bytes with a 16-byte blob:
/// <code>
/// 2517 OnFuncObject_Cancel tag 58357 [u32 0][u32 targetHandle][u32 systemMessageId] to the canceller
/// 2518 OnFuncObject_CancelNotify tag 58358 [u32 0][u32 pcHandle][u32 targetHandle] to nearby players
/// </code>
/// 2517 clears the client's control slot when it names the same target; a non-zero systemMessageId is shown as an
/// error. 2518 returns the player to idle, which stops the gather animation.
/// </para>
/// <para>
/// Every cancel takes the success path: systemMessageId is 0 (no message) and 2518 is sent.
/// </para>
/// <para>
/// On the first cancel of a running control, the canceller's state is reset to idle with a 2398 update before the
/// 2517. A repeated cancel gets 2517 and 2518 again, without a second state update. See FuncObjectControlState.
/// </para>
/// <para>
/// Both reply bodies use the standard message envelope.
/// </para>
/// </remarks>
public class NpcInteractCancelHandler : ZoneRmiHandlerBase<NpcInteractCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFunctionObjectControlCancel;
    public override string Name => "NpcInteractCancel";

 /// <summary>msgTag of 2517 OnFuncObject_Cancel (58357).</summary>
    private const ushort TagFuncObjectCancel = 58357;

 /// <summary>msgTag of 2518 OnFuncObject_CancelNotify (58358).</summary>
    private const ushort TagFuncObjectCancelNotify = 58358;

    public NpcInteractCancelHandler(IServiceScopeFactory s, ILogger<NpcInteractCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var targetHandle = reader.ReadUInt32();

        Logger.LogDebug("Session {Id}: {Name} cancels control of {Target:X8}", session.SessionId, player.Name, targetHandle);

        
        
        
        
        
        
        
        
        
        
        
        
        
        if (FuncObjectControlState.TryEnd(player, targetHandle, out var control) && control.State is not null)
        {
            var reset = FuncObjectControlPackets.BuildControlReset2398(player.EntityId, targetHandle);
            await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(reset));
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(reset));
        }

        var cancel = BuildFuncObjectCancel2517(targetHandle, systemMessageId: 0);
        await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObject_Cancel, writer => writer.WriteBytes(cancel));

        
        var notify = BuildFuncObjectCancelNotify2518(player.EntityId, targetHandle);
        await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObject_CancelNotify, writer => writer.WriteBytes(notify));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnFuncObject_CancelNotify,
            writer => writer.WriteBytes(notify));
    }

 /// <summary>2517 body: <c>[01 10][u16 16][u16 58357][u32 0][u32 targetHandle][u32 systemMessageId]</c>.</summary>
    internal static byte[] BuildFuncObjectCancel2517(uint targetHandle, uint systemMessageId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagFuncObjectCancel);
            p.WriteUInt32(0);
            p.WriteUInt32(targetHandle);
            p.WriteUInt32(systemMessageId);
        });
        return pw.ToArray();
    }

 /// <summary>2518 body: <c>[01 10][u16 16][u16 58358][u32 0][u32 pcHandle][u32 targetHandle]</c>.</summary>
    internal static byte[] BuildFuncObjectCancelNotify2518(uint pcHandle, uint targetHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagFuncObjectCancelNotify);
            p.WriteUInt32(0);
            p.WriteUInt32(pcHandle);
            p.WriteUInt32(targetHandle);
        });
        return pw.ToArray();
    }
}
