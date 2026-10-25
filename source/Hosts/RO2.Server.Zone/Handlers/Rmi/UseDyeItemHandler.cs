using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1575 ReqUseDyeItem: dyes a piece of gear, answered by 2835 AnsUseDyeItem.</summary>
/// <remarks>
/// <para>
/// Sent when the player confirms a colour in the dye editor. The client selects the colour and sends the
/// point it chose; the server reads the colour at that point of the dye's colour picker bitmap. Request and
/// answer layouts are in <see cref="DyePackets"/>, and the colour lookup is in <see cref="DyeColorCatalog"/>.
/// </para>
/// <para>
/// On success, in order:
/// <list type="number">
/// <item>The colour is stored in the target's dye slot named by request byte 24 (slots 0 to 2). Every later
/// record of the item carries it, as does the worn record other players see.</item>
/// <item>One dye is consumed: its count drops by one, and the stack is removed at 0.</item>
/// <item>2835 is sent with the result. For a worn target, a self 2315 OnPCViewData is sent with the new colour.
/// No 2848 is sent to other players.</item>
/// </list>
/// The 2396 OnSocialAni that often follows is not sent by this handler; the client requests it separately (1369).
/// </para>
/// <para>
/// Refused with code 1, consuming nothing, when the dye is not an owned Item_Type 8 bag stack with a
/// ColorPickerInfo row; when the target is not the player's own bag or worn item, or is the dye itself;
/// when the slot is above 2; or when the pick resolves to no colour.
/// </para>
/// </remarks>
public class UseDyeItemHandler : ZoneRmiHandlerBase<UseDyeItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqUseDyeItem;
    public override string Name => "UseDyeItem";

    private const byte DyeItemType = 8;       

    public UseDyeItemHandler(IServiceScopeFactory s, ILogger<UseDyeItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < DyePackets.RequestLength) return;
        var request = payload.Slice(0, DyePackets.RequestLength).ToArray();
        ulong dyeUid = DyePackets.DyeUid(request);
        ulong targetUid = DyePackets.TargetUid(request);
        uint pick = DyePackets.Pick(request);
        byte dyeSlot = DyePackets.DyeSlot(request);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var dye = await inventory.GetByIdAsync(dyeUid);
        var target = targetUid != dyeUid ? await inventory.GetByIdAsync(targetUid) : null;

        uint? colour = Check(store, DyeColorCatalog.Shipped, player.CharacterId, dye, target, pick, dyeSlot, out var why);
        if (colour is not { } applied)
        {
            Logger.LogInformation("{Name} dye {Dye} on {Target} (slot {Slot}, pick {Pick:X8}) refused: {Why}",
                player.Name, dyeUid, targetUid, dyeSlot, pick, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsUseDyeItem, DyePackets.Build2835Refusal(DyePackets.ResultRefused, request), ct);
            return;
        }

        
        SetDyeSlot(target!, dyeSlot, applied);
        await inventory.UpdateItemAsync(target!);

        
        int left = dye!.Count - 1;
        if (left <= 0) { await inventory.RemoveItemAsync(dye.Id); left = 0; }
        else { dye.Count = left; await inventory.UpdateItemAsync(dye); }

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsUseDyeItem,
            DyePackets.Build2835Success((byte)Math.Clamp(left, 0, 255), request, applied), ct);

        
        
        if (target!.StorageType == 1 && player.Character is { } ch)
        {
            var worn = await inventory.GetEquipmentAsync(player.CharacterId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, ActorStatePackets.BuildOnPCViewData2315(player, ch, worn, services.GetRequiredService<IGuildEmblemRepository>()), ct);
        }

        Logger.LogInformation("{Name} dyed item {Target} (uid {TUid}) slot {Slot} 0x{Colour:X8} with dye {Dye} (pick {Pick:X8}), {Left} left",
            player.Name, target.ItemId, targetUid, dyeSlot, applied, dye.ItemId, pick, left);
    }

 /// <summary>
 /// The colour a 1575 gives, or null with the reason when it is refused (see the class remarks for the checks).
 /// </summary>
    internal static uint? Check(GameDataStore store, DyeColorCatalog? catalog, uint characterId, InventoryItem? dye,
        InventoryItem? target, uint pick, byte dyeSlot, out string why)
    {
        why = "";
        if (dye is null || dye.CharacterId != characterId || dye.StorageType != 0 || dye.Count < 1
            || store.GetItem(dye.ItemId)?.Item_Type != DyeItemType)
        {
            why = "dye is not an owned dye stack in the bag";
            return null;
        }
        if (target is null || target.CharacterId != characterId || target.StorageType is not (0 or 1) || target.Id == dye.Id)
        {
            why = "target is not the player's own bag or worn item";
            return null;
        }
        if (dyeSlot > DyePackets.MaxDyeSlot)
        {
            why = $"dye slot {dyeSlot} is not 0..{DyePackets.MaxDyeSlot}";
            return null;
        }
        if (catalog is null)
        {
            why = "no dye colour file (" + DyeColorCatalog.ShippedPath + ")";
            return null;
        }
        var colour = catalog.Resolve(store, dye.ItemId, pick);
        if (colour is null)
            why = $"dye {dye.ItemId} gives no colour for pick {pick:X8}";
        return colour;
    }

 /// <summary>Store <paramref name="colour"/> in dye slot <paramref name="slot"/> (0..2) of <paramref name="item"/>.</summary>
    internal static void SetDyeSlot(InventoryItem item, byte slot, uint colour)
    {
        switch (slot)
        {
            case 0: item.DyeColor = colour; break;
            case 1: item.DyeColor2 = colour; break;
            case 2: item.DyeColor3 = colour; break;
            default: throw new ArgumentOutOfRangeException(nameof(slot), slot, "an item has dye slots 0..2");
        }
    }
}
