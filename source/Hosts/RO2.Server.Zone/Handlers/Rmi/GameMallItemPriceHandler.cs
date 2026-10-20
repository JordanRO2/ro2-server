using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1601 ReqGameMallItemPrice: <c>[CompactLen n][n x u32 itemId]</c> (read_side CompactLen + Vec&lt;U32&gt;).
/// Answered with 2873 AnsGameMallItemPrice as result 0 and an empty price vector.
/// </summary>
/// <remarks>
/// <para>
/// UNREACHABLE FROM THE CLIENT, NO RETAIL FRAME. b303 never sends 1601: its only builder
/// sits in a game-vtable slot that no instruction calls. The answer's shape comes from the
/// client's reader alone.
/// </para>
/// <para>
/// 2873 reader: u16 result, then a compact length <c>[u8 width][width bytes LE]</c>,
/// then n x a bare 12-byte element. The consumer shows a cash-shop result message for a non-zero result and
/// on both paths destroys the vector WITHOUT reading an element. So the 12-byte element's layout is not recoverable
/// from the client, and no entry is sent even though data/mall/GameMallCatalog2866.json holds prices: any
/// {itemId, price, ...} split would be invented, and the client discards it anyway.
/// </para>
/// <para>
/// The old answer, <c>[u16 0][u32 0][u32 0]</c>, put a zero byte where the reader expects a compact-length width marker
/// (1/2/4/8), so ReadLength failed on it.
/// </para>
/// </remarks>
public class GameMallItemPriceHandler : ZoneRmiHandlerBase<GameMallItemPriceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallItemPrice;
    public override string Name => "GameMallItemPrice";
    public GameMallItemPriceHandler(IServiceScopeFactory s, ILogger<GameMallItemPriceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// The item ids of a 1601 body: a compact count, then that many u32. Ids past the end of the body are not read
 /// (the list is truncated, not rejected); null when the count prefix itself is malformed.
 /// </summary>
    public static uint[]? ParseRequestedIds(ReadOnlySpan<byte> payload)
    {
        if (!payload.TryReadCompactLength(out ulong count, out int prefix)) return null;
        var rest = payload[prefix..];
        int n = (int)Math.Min(count, (ulong)(rest.Length / 4));
        var ids = new uint[n];
        for (int i = 0; i < n; i++)
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4 * i));
        return ids;
    }

 /// <summary>2873 body: <c>[u16 result = 0][CompactLen 0 = 01 00]</c>, the reader's empty vector.</summary>
    public static byte[] BuildAnsGameMallItemPrice2873Empty()
    {
        using var w = new PacketWriter(8);
        w.WriteUInt16(0);
        w.WriteCompactLength(0);
        return w.GetWrittenSpan().ToArray();
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var ids = ParseRequestedIds(payload.Span);
        Logger.LogInformation("{Name} asks game mall prices for {Ids}", player.Name,
            ids is null ? "(malformed body)" : string.Join(",", ids));

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallItemPrice, BuildAnsGameMallItemPrice2873Empty(), ct);
    }
}
