using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1595 ReqGameMallItemSalesCount -> 2867 AnsGameMallItemSalesCount: a cash-mall sales-count query the
/// b303 client never sends. Answered with result 0, the answer the client ignores.</summary>
/// <remarks>
/// <para>
/// <b>Request</b>: one u32 at body+0 (4 bytes). The b303 client never sends it,
/// so this path is dead in practice.
/// </para>
/// <para>
/// <b>Answer</b>: 2867, no envelope, fixed 10 bytes
/// <c>[u16 result][u32][u32]</c>. The client's consumer
/// returns at once when the result is 0 and otherwise shows a result message; it never
/// reads the two u32s. This server keeps no per-product sales counter, so they go out as 0: unobservable on result
/// 0. The old answer was 4 bytes <c>[u16 0][u16 0]</c>, shorter than the reader's 10.
/// </para>
/// </remarks>
public class GameMallItemSalesCountHandler : ZoneRmiHandlerBase<GameMallItemSalesCountHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallItemSalesCount;
    public override string Name => "GameMallItemSalesCount";
    public GameMallItemSalesCountHandler(IServiceScopeFactory s, ILogger<GameMallItemSalesCountHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>2867 body: <c>[u16 result][u32][u32]</c>, 10 bytes, no envelope (see remarks).</summary>
    internal static byte[] BuildAnsGameMallItemSalesCount2867(ushort result, uint first, uint second)
    {
        var body = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), first);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), second);
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: 1595 ReqGameMallItemSalesCount too short ({Len} bytes); not answered", player.Name, payload.Length);
            return;
        }
        uint requested = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        Logger.LogDebug("{Name} queries game mall sales count for {Requested}; no sales counter kept, answering 0",
            player.Name, requested);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallItemSalesCount,
            BuildAnsGameMallItemSalesCount2867(0, 0, 0), ct);
    }
}
