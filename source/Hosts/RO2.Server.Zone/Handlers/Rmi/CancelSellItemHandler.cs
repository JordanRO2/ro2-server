using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

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
    public CancelSellItemHandler(IServiceScopeFactory s, ILogger<CancelSellItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>The failure code sent on 2377 for a refused buy-back. It is a
    /// system-message id the client resolves itself.</summary>
    internal const short RefusedResult = 1;

    /// <summary>
    /// Refusal code for a buy-back with no free bag cell: 20020, the "no free bag cell" code retail uses in 2375
    /// (BuyItemHandler.BuyFailBagFull), 2438 and 2801. The client already renders this code, so it is reused
    /// here for the bag-full buy-back case.
    /// </summary>
    internal const short BagFullResult = 20020;

    /// <summary>Blob length of 2377.</summary>
    internal const int BlobLength = 152;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Payload = [u8 marker][marker-byte length][blob]; the marker is 1.
        // Lenient: the declared length is not compared, only that the blob reaches the uid.
        if (!payload.Span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < 16) return;
        var uid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)); // blob+8

        if (!BuyBackLedger.TryTake(player, uid, out var entry, out var index))
        {
            // Not on the buy-back list: already bought back (the usual refusal), or a
            // sale this server has no copy of, so there is no item to give back.
            Logger.LogInformation("{Name} tried to buy back uid {Uid}, which is not on the buy-back list; refused",
                player.Name, uid);
            await SendAsync(session, Build2377(player.EntityId, RefusedResult, player.Gold, null, uid), ct);
            return;
        }

        // The price is taken here, atomically; the catch below hands it back if the item cannot be written.
        if (!player.TryDebit(entry.Paid))
        {
            // This case gets the one failure code used on this opcode, and nothing is charged
            // or given.
            BuyBackLedger.Restore(player, entry, index);
            Logger.LogInformation("{Name} cannot afford to buy back uid {Uid} ({Cost}g, has {Have}g)",
                player.Name, uid, entry.Paid, player.Gold);
            await SendAsync(session, Build2377(player.EntityId, RefusedResult, player.Gold, null, uid), ct);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var items = services.GetRequiredService<GameDataStore>().Items;
        var sold = entry.Item;
        var restored = new InventoryItem
        {
            Id = sold.Id,
            CharacterId = player.CharacterId,
            ItemId = sold.ItemId,
            Count = sold.Count,
            Slot = -1,         // set below to the first free bag cell
            StorageType = 0,   // bag
            CreatedAt = sold.CreatedAt,
        }.CopyInstanceStateFrom(sold); // the same instance back: the record differs only at +14..+16
        bool placed;
        try
        {
            // The cell comes from BagCapacity, not from AddItemAsync's Slot -1, which takes the lowest unused index
            // with no upper bound (a full bag would put the item in a cell no bag provides). The pick and the add
            // run under the character's inventory gate so a concurrent add cannot take the same cell between them.
            placed = await CharacterInventoryGate.RunAsync(player.CharacterId, async () =>
            {
                var free = BagCapacity.FreeCells(await inventory.GetByCharacterAsync(player.CharacterId), items);
                if (free.Count == 0) return false;
                restored.Slot = free[0];

                // Retail gives back the same uid (record uid == requested uid). This server's uids
                // come from max(id)+1, so the id of a deleted stack can have been handed out again, and a
                // partly sold stack still has it. Only then does the item get a new uid; +144 still echoes
                // the requested one, which is the uid the client's buy-back entry is keyed by.
                if (await inventory.GetByIdAsync(restored.Id) is not null)
                    restored.Id = 0;
                await inventory.AddItemAsync(restored);
                return true;
            });
        }
        catch (Exception ex)
        {
            player.Credit(entry.Paid);
            BuyBackLedger.Restore(player, entry, index);
            Logger.LogWarning(ex, "{Name}: buy-back of uid {Uid} could not be written; refused", player.Name, uid);
            await SendAsync(session, Build2377(player.EntityId, RefusedResult, player.Gold, null, uid), ct);
            return;
        }

        if (!placed)
        {
            // Bag full: hand the price back, keep the sale on the buy-back list, deliver nothing.
            player.Credit(entry.Paid);
            BuyBackLedger.Restore(player, entry, index);
            Logger.LogInformation("{Name}: no free bag cell to buy back uid {Uid}; refused", player.Name, uid);
            await SendAsync(session, Build2377(player.EntityId, BagFullResult, player.Gold, null, uid), ct);
            return;
        }

        // Write the wallet through, as the sale did, so a crash cannot keep the item and the zeny.
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);

        Logger.LogInformation("{Name} bought back uid {Uid} (item {Item} x{Count}) for {Cost}g",
            player.Name, uid, restored.ItemId, restored.Count, entry.Paid);

        var record = WorldEntryService.BuildItemRecord126(restored);
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
