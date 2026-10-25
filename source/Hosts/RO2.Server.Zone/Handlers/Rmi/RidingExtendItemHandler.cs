using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1691 (ReqRidingExtendItem), which extends a timed vehicle with an extension item from the
/// player's bag. The reply is 2329 OnUseItem for the consumed item and 3023 AnsRidingExtendItem with the new
/// expiry.
/// </summary>
/// <remarks>
/// The request body is <c>[u32 ridingId][u64 extensionItemUid]</c>. The extension length comes from the item's
/// ItemCashInfo duration, and an item extends only the vehicles whose reins items accept its extension group.
/// Requests the character cannot fulfil are ignored and the item is not consumed.
/// </remarks>
public class RidingExtendItemHandler : ZoneRmiHandlerBase<RidingExtendItemHandler>
{
    private const int BodyLength = 12;

 /// <summary>ItemCashInfo.Duration_Type value for items that last a number of days.</summary>
    private const byte DurationDays = 1;

 /// <summary>Source value of the 2329 reply that marks a vehicle extension.</summary>
    private const byte UseSourceExtend = 1;

    public override ushort Opcode => ZoneC2SOpcodes.ReqRidingExtendItem;
    public override string Name => "RidingExtendItem";
    public RidingExtendItemHandler(IServiceScopeFactory s, ILogger<RidingExtendItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return;
        var ridingId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        var extensionItemUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span.Slice(4));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var ticket = await inventory.GetByIdAsync(extensionItemUid);
        if (ticket is null || ticket.CharacterId != player.CharacterId || ticket.StorageType == 1 || ticket.Count <= 0)
        {
            Logger.LogInformation("{Name}: 1691 names item uid {Uid}, which the player does not hold in a bag", player.Name, extensionItemUid);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        if (!TryResolveExtension(store, ridingId, ticket.ItemId, out var days))
        {
            Logger.LogInformation("{Name}: item {Item} does not extend vehicle {Vehicle} (DurationExtendGroup / ItemCashInfo)",
                player.Name, ticket.ItemId, ridingId);
            return;
        }

        var nowOle = VehiclePackets.NowOle();
        IReadOnlyList<OwnedVehicle>? vehicles;
        try
        {
            vehicles = await services.GetRequiredService<IVehicleRepository>()
                .TryExtendTimedAsync(player.CharacterId, ridingId, days, nowOle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: could not store the extension of vehicle {Vehicle}; item not consumed", player.Name, ridingId);
            return;
        }
        if (vehicles is null)
        {
            Logger.LogInformation("{Name}: vehicle {Vehicle} is not a timed vehicle the character owns", player.Name, ridingId);
            return;
        }
        var expiry = vehicles.First(v => v.RidingId == ridingId).ExpiryOle;

        var remaining = ticket.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(ticket.Id); remaining = 0; }
        else { ticket.Count = remaining; await inventory.UpdateItemAsync(ticket); }

        var (bag, slot) = ClientBagSlot(ticket);
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            writer => writer.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, ticket.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceExtend)));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsRidingExtendItem, BuildAnsRidingExtendItem3023(ridingId, expiry, nowOle));
        Logger.LogInformation("{Name} extended vehicle {Vehicle} by {Days} days with item {Item}", player.Name, ridingId, days, ticket.ItemId);
    }

 /// <summary>
 /// Returns the number of days <paramref name="itemId"/> adds to <paramref name="ridingId"/>, if one of the
 /// vehicle's reins items accepts the item's extension group.
 /// </summary>
    internal static bool TryResolveExtension(GameDataStore store, uint ridingId, uint itemId, out uint days)
    {
        days = 0;
        var bit = store.DurationExtendGroup.FirstOrDefault(g => g.Item_ID == itemId)?.DurationExtend_ID ?? 0;
        if (bit == 0) return false;
        var cash = store.ItemCashInfo.FirstOrDefault(c => c.Item_ID == itemId);
        if (cash is not { Duration_Type: DurationDays, Duration_Time: > 0 }) return false;
        var vehicle = store.RidingInfo.FirstOrDefault(r => r.ID == ridingId);
        if (vehicle is null) return false;
        var reins = store.RidingDurationGroup.Where(g => g.RidingDuration_ID == vehicle.RidingDuration_GroupID)
            .Select(g => g.Item_ID).ToHashSet();
        int accepted = 0;
        foreach (var c in store.ItemCashInfo)
            if (reins.Contains(c.Item_ID)) accepted |= c.DurationExtend_GroupID;
        if ((accepted & bit) == 0) return false;
        days = cash.Duration_Time;
        return true;
    }

 /// <summary>
 /// Builds the 3023 AnsRidingExtendItem body (17 bytes): <c>[u16 result 0][u32 ridingId][f64 expiry][u8 1][u8 state][u8 0]</c>.
 /// </summary>
    internal static byte[] BuildAnsRidingExtendItem3023(uint ridingId, double expiryOle, double nowOle)
    {
        using var pw = new PacketWriter(17);
        pw.WriteUInt16(0);
        pw.WriteUInt32(ridingId);
        pw.WriteDouble(expiryOle);
        pw.WriteByte(1);
        pw.WriteByte(VehiclePackets.StateOf(expiryOle, nowOle));
        pw.WriteByte(0);
        return pw.ToArray();
    }

 /// <summary>Returns the client's (bag, slot) cell for a bag item, as used by the 2329 reply.</summary>
    private static (byte bag, byte slot) ClientBagSlot(RO2.Inventory.Domain.InventoryItem it)
    {
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < 20) return (1, (byte)(50 + flat));
        var i = flat - 20;
        return ((byte)Math.Clamp(2 + i / 35, 2, 4), (byte)(i % 35));
    }
}
