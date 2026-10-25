using System;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1680 ReqDirectReInforce, the immediate refine stone that sets an item to the stone's refine level.</summary>
/// <remarks>
/// The request body starts with two u64 item instance ids. The stone and the target are told apart by item type
/// (66 for weapon stones, 67 for armor stones). The target's refine level is set to the stone's level and one stone
/// is consumed. The reply, AnsDirectReInforce (3012), is a fixed 128-byte body: a u16 result followed by the 126-byte
/// item record. A result of 0 carries the updated target. Any rejection replies with result 1 and an empty record,
/// and changes nothing.
/// </remarks>
public class DirectReInforceHandler : ZoneRmiHandlerBase<DirectReInforceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDirectReInforce;
    public override string Name => "DirectReInforce";
    public DirectReInforceHandler(IServiceScopeFactory s, ILogger<DirectReInforceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int MaxRefine = 20;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 16) { await RejectAsync(session, player); return; }
        var blob = payload.Span;
        ulong uidA = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(0));   
        ulong uidB = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8));   

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var itemA = uidA != 0 ? await inventory.GetByIdAsync(uidA) : null;
        var itemB = uidB != 0 ? await inventory.GetByIdAsync(uidB) : null;

        
        RO2.Inventory.Domain.InventoryItem? stone = null, target = null;
        RO2.Domain.GameData.ItemData? stoneInfo = null;
        foreach (var it in new[] { itemA, itemB })
        {
            if (it is null || it.CharacterId != player.CharacterId) continue;
            if (store.Items.TryGetValue(it.ItemId, out var info) && (info.Item_Type == 66 || info.Item_Type == 67))
            { stone = it; stoneInfo = info; }
            else target = it;
        }
        if (stone is null || stoneInfo is null || target is null || stone.Id == target.Id)
        {
            await RejectAsync(session, player); return;
        }

        
        
        if (!store.Items.TryGetValue(target.ItemId, out var targetInfo)) { await RejectAsync(session, player); return; }
        bool wantWeapon = stoneInfo.Item_Type == 66;
        bool targetIsWeapon = targetInfo.Weapon_Type != 0;
        bool targetIsArmor = targetInfo.Armor_Type != 0;
        if ((wantWeapon && !targetIsWeapon) || (!wantWeapon && !targetIsArmor))
        {
            await RejectAsync(session, player); return;
        }

        int level = Math.Clamp((int)stoneInfo.Item_Type_Option, 0, MaxRefine);
        target.Refine = (byte)level;
        await inventory.UpdateItemAsync(target);

        
        (byte sBag, byte sSlot) = ClientBagSlot(stone);
        int rem = stone.Count - 1;
        if (rem <= 0) { await inventory.RemoveItemAsync(stone.Id); rem = 0; }
        else { stone.Count = rem; await inventory.UpdateItemAsync(stone); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, sSlot, sBag, stone.Id,
                (byte)Math.Clamp(rem, 0, 255), useSource: 1)));

        
        if (target.StorageType == 1 && player.Character is { } ch)
        {
            var worn = await inventory.GetEquipmentAsync(ch.Id);
            
            
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn));
            (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, worn);
            
            await EquipViewNotifyPackets.BroadcastEquipChangeAsync(Broadcast, player, worn, services.GetRequiredService<IGuildEmblemRepository>());
        }

        
        
        var rec = WorldEntryService.BuildItemRecord126(target);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsDirectReInforce, w =>
        {
            w.WriteUInt16(0);        
            w.WriteBytes(rec);       
        });

        Logger.LogInformation("{Name}: immediate stone {Stone} -> item {Item} set to +{Lvl} ({Kind})",
            player.Name, stone.ItemId, target.Id, level, wantWeapon ? "weapon" : "armor");
    }

 /// <summary>Replies to a refused request with result 1 and an empty item record.</summary>
    private static Task RejectAsync(ProudSession session, PlayerState player) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsDirectReInforce, w =>
        {
            w.WriteUInt16(1);            
            w.WriteZeros(126);           
        });

 /// <summary>Maps an item's storage slot to the client's bag and slot coordinates.</summary>
    private static (byte bag, byte slot) ClientBagSlot(RO2.Inventory.Domain.InventoryItem it)
    {
        if (it.StorageType == 1) return (0, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, 49));
        if (it.StorageType == 2) return (5, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BankExpansion.MaxBankSlots - 1));
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < 20) return (1, (byte)(50 + flat));
        var i = flat - 20;
        return ((byte)Math.Clamp(2 + i / 35, 2, 4), (byte)(i % 35));
    }
}
