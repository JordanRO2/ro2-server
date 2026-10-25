using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1548 ReqUseCashItem: uses a card album, a timed cash item that charges the card book.</summary>
/// <remarks>
/// <para>
/// Request: 8 bytes, the u64 instance uid of the item.
/// </para>
/// <para>
/// On success the server sends 2379 OnDestroyItem for the album (count 0), then 2793 NfyCardBookChargedTime
/// with the new expiry, then 2789 AnsUseCashItem with result 0.
/// </para>
/// <para>
/// The expiry is an OLE automation date on the server's local clock. A first charge lasts the album's
/// Duration_Time in days from now. Using an album while the book is still charged extends the current expiry
/// by 30 days instead.
/// </para>
/// <para>
/// A refused request is answered with 2789 result 24000 and consumes nothing. This covers unknown uids and
/// uids that were already consumed.
/// </para>
/// <para>
/// At each world entry the server re-sends 2793 for a character whose card book has ever been charged, with
/// the stored expiry, or with a lapsed marker once the charge has ended. The expiry is persisted per character
/// (<see cref="CardBookChargeStore"/>). <see cref="CardBookChargeExpiryNotifier"/> sends 2794
/// NfyCardBookChargedTimeEnd when the charge lapses while the character is still in the world.
/// </para>
/// </remarks>
public class UseCashItemHandler : ZoneRmiHandlerBase<UseCashItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUseCashItem;
    public override string Name => "UseCashItem";

 /// <summary>Expected length of the 1548 request body, in bytes.</summary>
    private const int RequestBodyLength = 8;

 /// <summary>ItemInfo Item_Type of the card album items accepted by this request.</summary>
    private const byte CardAlbumItemType = 28;

 /// <summary>ItemCashInfo.Duration_Type of card albums: Duration_Time counts days.</summary>
    private const byte DurationInDays = 1;

 /// <summary>2789 result sent for a uid that was already consumed.</summary>
    private const ushort UseCashItemNoSuchItem = 24000;

    public UseCashItemHandler(IServiceScopeFactory s, ILogger<UseCashItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: UseCashItem body is {Len} bytes, not the 8 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        ulong itemUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var item = await inventory.GetByIdAsync(itemUid);

        string? why = null;
        RO2.GameData.Tables.ItemCashInfo? cash = null;
        if (item is null || item.CharacterId != player.CharacterId || item.StorageType != 0 || item.Count < 1)
            why = "no such item in the bag";
        else if (store.GetItem(item.ItemId)?.Item_Type != CardAlbumItemType)
            why = $"item {item.ItemId} is not a card album (Item_Type {CardAlbumItemType})";
        else
        {
            uint templateId = item.ItemId;
            cash = store.ItemCashInfo.FirstOrDefault(c => c.Item_ID == templateId);
            if (cash is null || cash.Duration_Type != DurationInDays || cash.Duration_Time == 0)
                why = $"item {templateId} has no day-based ItemCashInfo duration";
        }

        if (why is not null)
        {
            Logger.LogInformation("{Name} use-cash-item {Uid} refused: {Why}", player.Name, itemUid, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsUseCashItem, writer => writer.WriteUInt16(UseCashItemNoSuchItem));
            return;
        }

        
        int left = item!.Count - 1;
        byte[] record;
        if (left <= 0)
        {
            record = WorldEntryService.BuildItemRecord126(item);
            record[14] = 0;   
            await inventory.RemoveItemAsync(item.Id);
        }
        else
        {
            item.Count = left;
            await inventory.UpdateItemAsync(item);
            record = WorldEntryService.BuildItemRecord126(item);
        }
        await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
            writer => writer.WriteBytes(ShopItemPackets.BuildDestroyItem2379(player.EntityId, record)));

        
        
        
        int days = cash!.Duration_Time;
        var charges = services.GetRequiredService<ICardBookChargeRepository>();
        DateTime chargedUntil = CardBookChargeStore.ToServerLocal(
            await charges.ExtendChargeAsync(player.CharacterId, days, DateTime.UtcNow, ct));

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyCardBookChargedTime,
            writer => writer.WriteBytes(CardBookChargeStore.Build2793(chargedUntil, DateTime.Now)));

        
        
        CardBookChargeExpiryNotifier.Arm(session, player.CharacterId, chargedUntil, PlayerManager,
            charges, Logger);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsUseCashItem, writer => writer.WriteUInt16(0));

        Logger.LogInformation("{Name} used card album {Item} ({Uid}): card book charged until {Until:yyyy-MM-dd HH:mm} (+{Days} days)",
            player.Name, item.ItemId, item.Id, chargedUntil, days);
    }
}
