using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1336 ReqCancelSellItem: buy back a stack sold to an NPC shop, answered by RMI 2377
/// OnCancelSellItem.
/// </summary>
/// <remarks>
/// <para>
/// Request: an 18-byte body,
/// <c>[01 10][u16 innerSize=16][u16 msgTag=58052][u32 PC handle][u64 sold item uid]</c>, so the uid is
/// at blob+8.
/// </para>
/// <para>
/// Reply: blob of 152 bytes behind a 2-byte length (marker 2),
/// <c>[u16 152][u16 58053][u32 handle@4][i16 result@8][u64 wallet@10][126-byte item record@18]
/// [u64 uid@144]</c>. The handle echoes the request's and the uid at +144 echoes the requested uid. The
/// client sets the wallet from +10, adds the record at +18 to the bag, and uses +144.
/// </para>
/// <para>
/// A success charges what the sale paid: the wallet at +10 is the previous wallet minus
/// Price_Sell x count, and the record is the sold item with only its count and bag/slot changed (see
/// <see cref="BuyBackLedger"/>). A refusal is result 1, the CURRENT wallet at +10, an all-zero record, and
/// the uid still echoed. Retail's only refusal is a repeat request for a uid already bought back.
/// </para>
/// <para>
/// A refusal must not carry result 0: the client would read it as a successful buy-back, set the displayed
/// zeny to 0 and receive an empty item.
/// </para>
/// </remarks>
public class CancelSellItemHandler : ZoneRmiHandlerBase<CancelSellItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCancelSellItem;
    public override string Name => "CancelSellItem";

    /// <summary>The shop economy flow; stateless, every dependency is passed per call.</summary>
    private static readonly ShopApplicationService Shop = new();

    public CancelSellItemHandler(IServiceScopeFactory s, ILogger<CancelSellItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>The failure code sent on 2377 for a refused buy-back. It is a
    /// system-message id the client resolves itself.</summary>
    internal const short RefusedResult = ShopCodes.CancelRefused;

    /// <summary>Blob length of 2377.</summary>
    internal const int BlobLength = 152;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Payload = [u8 marker][marker-byte length][blob]; the marker is 1.
        // Lenient: the declared length is not compared, only that the blob reaches the uid.
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < 16) return;
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)); // blob+8

        // The take from the buy-back list, the price, the write and the refunds are the economy flow's; it sends
        // nothing. This handler answers with the 2377: a refusal is its code with the CURRENT wallet and an all-zero
        // record, a success the restored item.
        var characters = services.GetRequiredService<ICharacterRepository>();
        var result = await Shop.BuyBackAsync(new ShopBuyBackCommand(player.CharacterId, player.Name, uid), player,
            BuyBackLedger.ListOf(player),
            new ShopInventoryAdapter(services.GetRequiredService<IInventoryRepository>(),
                services.GetRequiredService<GameDataStore>().Items, Logger),
            new ShopLogAdapter(Logger),
            () => characters.UpdateGoldAsync(player.CharacterId, player.Gold));

        if (result.Restored is not { } restored)
        {
            await SendAsync(session, Build2377(player.EntityId, result.Code, player.Gold, null, uid), ct);
            return;
        }

        var record = ItemPackets.BuildItemRecord126(InventoryEscrowAdapter.ToInventoryItem(restored, player.CharacterId));
        await SendAsync(session, Build2377(player.EntityId, 0, player.Gold, record, uid), ct);
    }

    private static Task SendAsync(ProudSession session, byte[] body, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.OnCancelSellItem, w => w.WriteBytes(body), ct);

    /// <summary>
    /// Builds RMI 2377 OnCancelSellItem (layout in the class remarks). <paramref name="record"/> is the
    /// bought-back item, or <c>null</c> for a refusal, which carries an all-zero record like retail's.
    /// </summary>
    internal static byte[] Build2377(uint actorHandle, short result, long wallet, byte[]? record, ulong requestedUid)
    {
        if (record is not null && record.Length != ItemPackets.ItemRecordLength)
            throw new ArgumentException($"item record must be {ItemPackets.ItemRecordLength} bytes", nameof(record));

        using var pw = new PacketWriter(BlobLength + 3);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(ShopItemPackets.MsgTagCancelSellItem);  // blob+2  58053
            p.WriteUInt32(actorHandle);                           // blob+4  the requester's handle
            p.WriteUInt16((ushort)result);                        // blob+8  0 ok, 1 refused
            p.WriteUInt64((ulong)Math.Max(0, wallet));            // blob+10 wallet after the request
            if (record is not null) p.WriteBytes(record);         // blob+18 the item given back
            else p.WriteBytes(new byte[ItemPackets.ItemRecordLength]);
            p.WriteUInt64(requestedUid);                          // blob+144 requested uid, echoed
        });
        return pw.ToArray();
    }
}
