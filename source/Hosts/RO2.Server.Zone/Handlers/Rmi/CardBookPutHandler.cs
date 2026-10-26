using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1546 ReqCardBookPut - put a card from the bag into the card book.
/// Request (MEASURED, NONE envelope, Blob13): [u8 kind @0][u8 @1][u64 itemUid @2 (unaligned)][3 bytes @10].
/// In the 106 accepted archive puts, against the 2379 record of the stack that left: @0/@1 are its bag and
/// slot (106/106), @10 its count (the last recorded bag count in 76/76 where a non-zero one is on record),
/// @11 its enforce grade (== record+0x1D, 106/106), and @12 a byte retail echoes into the 2589 (106/106).
/// The stack is moved (ownership-checked) into the Mongo card book via CardService; the item row is
/// deleted so it never duplicates. Answers like retail: 2379 (the stack leaves the bag), 2589 (the book
/// record of the card), then AnsCardBookPut (2791).</summary>
public class CardBookPutHandler : ZoneRmiHandlerBase<CardBookPutHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardBookPut;
    public override string Name => "CardBookPut";

 /// <summary>Request offset of the u64 item uid of the stack being put (unaligned).</summary>
    private const int ItemUidOffset = 2;

 /// <summary>Smallest request that holds the item uid: offset 2 plus 8 bytes.</summary>
    private const int ItemUidEnd = ItemUidOffset + sizeof(ulong);

 /// <summary>Request offset of the book-kind byte (0 or 1); it is echoed into the 2589 record.</summary>
    private const int BookKindOffset = 12;

 /// <summary>Smallest request that holds the book-kind byte.</summary>
    private const int BookKindEnd = BookKindOffset + 1;

 /// <summary>Offset of the u8 stack count inside a 126-byte item record. 0 empties the client's bag cell.</summary>
    private const int ItemRecordCountOffset = 14;

 /// <summary>2589 record byte +4 (flag): always 1 in a card-book update.</summary>
    private const byte CardStatusRecordFlag = 1;

 /// <summary>2791 result code of an accepted put.</summary>
    private const ushort CardBookPutOk = 0;

    public CardBookPutHandler(IServiceScopeFactory s, ILogger<CardBookPutHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        ulong itemUid = 0;
        if (payload.Length >= ItemUidEnd)
            itemUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span.Slice(ItemUidOffset));

        
        
        byte[]? removedRecord = null;
        if (itemUid != 0)
        {
            var item = await services.GetRequiredService<IInventoryRepository>().GetByIdAsync(itemUid);
            if (item is not null && item.CharacterId == player.CharacterId)
                removedRecord = ItemPackets.BuildItemRecord126(item);
        }

        
        
        
        byte kind = payload.Length >= BookKindEnd && payload.Span[BookKindOffset] != 0 ? (byte)1 : (byte)0;

        var svc = CardService.From(services, Logger);
        var (ok, cardId, enforceGrade, copiesInBook) = await svc.PutCardAsync(player.CharacterId, itemUid, kind, ct);

        Logger.LogInformation("{Name} puts card uid {Uid} -> card {CardId} +{Grade}, {Copies} in book (ok={Ok})",
            player.Name, itemUid, cardId, enforceGrade, copiesInBook, ok);

        
        
        
        
        if (ok && removedRecord is not null)
        {
            removedRecord[ItemRecordCountOffset] = 0;   
            var removed2379 = QuestProgressPackets.BuildItemRemovedWithReason2379(
                player.EntityId, QuestProgressPackets.ItemRemovedReasonCardBookPut, removedRecord);
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, writer => writer.WriteBytes(removed2379), ct);
        }

        
        
        
        
        
        
        
        
        
        if (ok)
        {
            var status = CardStatusPackets.BuildUpdateCardStatus2589(new[]
            {
                new CardStatusRecord(cardId, CardStatusRecordFlag, (byte)Math.Clamp(copiesInBook, 0, 255), enforceGrade, kind),
            });
            await session.SendRmiAsync(ZoneS2COpcodes.AnsUpdateCardStatus, writer => writer.WriteBytes(status), ct);
        }

        
        
        var putBody = ok
            ? CardPackets.BuildCardBookPut2791(CardBookPutOk, cardId)
            : CardPackets.BuildCardBookPut2791(CardPackets.CardBookPutFailed, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsCardBookPut, writer => writer.WriteBytes(putBody), ct);
    }
}
