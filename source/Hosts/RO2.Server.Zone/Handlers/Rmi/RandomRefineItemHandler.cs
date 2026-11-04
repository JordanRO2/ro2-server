using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1676 (ReqRandomRefineItem) = Honing: rolls an equip's 5 random grinding stat lines
/// (a PREFIX pair + a BONUS triple) and its range-rate, charging a Honing Fee and consuming a material.</summary>
/// <remarks>
/// Wire layout of the 50-byte blob (compact-BA inner):
/// <code>
/// blob+17 item descriptor, 11 bytes: [u8 slot][u8 bag][u8 count][u64 uid] (target item)
/// blob+28 item descriptor, 11 bytes (material)
/// blob+39 item descriptor, 11 bytes (unused)
/// </code>
/// Server-authoritative: the roll is <see cref="HoningRoll"/> (the measured retail distribution); the client
/// only displays the 3009 answer. The honed lines persist on the item (<see cref="InventoryItem.GrindingJson"/>,
/// and the range-rate on <see cref="InventoryItem.AbilityValue"/> at record+69), so they survive a relog.
/// Result codes: 0 = honed, 3 = no usable material, 6 = cannot be honed (not gear / too low / too poor).
/// </remarks>
public class RandomRefineItemHandler : ZoneRmiHandlerBase<RandomRefineItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomRefineItem;
    public override string Name => "RandomRefineItem";

 /// <summary>Expected length of the request body.</summary>
    private const int BlobLength = 50;
 /// <summary>Offset of the target item descriptor within the blob.</summary>
    private const int TargetDescriptor = 17;
 /// <summary>Offset of the material item descriptor within the blob.</summary>
    private const int MaterialDescriptor = 28;

 /// <summary>Result code 0: the item was honed; the 3009 record carries the new lines.</summary>
    private const ushort ResultHoned = 0;
 /// <summary>Result code 3: the request has no usable material.</summary>
    internal const ushort ResultNoMaterial = 3;
 /// <summary>Result code 6: the item cannot be honed (not gear, too low, or the fee could not be paid). Nothing is charged.</summary>
    private const ushort ResultCannotReroll = 6;

    public RandomRefineItemHandler(IServiceScopeFactory s, ILogger<RandomRefineItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        if (!body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: RandomRefineItem body is {Len} bytes, not the compact frame retail sends",
                session.SessionId, body.Length);
            return;
        }

        byte targetSlot = blob[TargetDescriptor];
        byte targetBag = blob[TargetDescriptor + 1];
        byte targetCount = blob[TargetDescriptor + 2];
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(TargetDescriptor + 3, 8));
        ulong materialUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(MaterialDescriptor + 3, 8));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        bool owned = target is not null && target.CharacterId == player.CharacterId;

        async Task AnswerAsync(ushort code, byte[] record) =>
            await session.SendRmiAsync(ZoneS2COpcodes.OnRandomRefineItem, w => w.WriteBytes(
                ShopItemPackets.BuildRandomRefineItem3009(player.EntityId, code, record)));

        byte[] TargetRecord() => owned ? ItemPackets.BuildItemRecord126(target!) : DescriptorEcho(targetUid, targetCount, targetBag, targetSlot);

        // ---- material: must be owned, in the bag, and a known Grinding_Item (honing hammer) ----
        var material = materialUid != 0 ? await inventory.GetByIdAsync(materialUid) : null;
        uint materialItem = material?.ItemId ?? 0;
        bool materialUsable = material is not null && material.CharacterId == player.CharacterId
            && material.StorageType == 0 && material.Count > 0
            && store.GrindingItem.Any(g => g.Item_ID == materialItem);
        if (!materialUsable)
        {
            await AnswerAsync(ResultNoMaterial, DescriptorEcho(targetUid, targetCount, targetBag, targetSlot));
            return;
        }

        // ---- target must be owned honeable gear (weapon/armour, Equip_Slot 1..7) ----
        if (!owned || !RefineItemHandler.IsRefineableEquipSlot(store.GetItem(target!.ItemId)?.Equip_Slot ?? 0))
        {
            await AnswerAsync(ResultCannotReroll, TargetRecord());
            return;
        }

        // ---- roll BEFORE charging: if the item cannot produce lines, nothing is taken ----
        uint av = ItemPackets.RecordAbilityValue(target.ItemId, null);   // the TEMPLATE ability value drives the roll's tiers
        var rolled = HoningRoll.Roll(av, store.GrindingAbili, store.GrindingPrefixAbili, Random.Shared);
        if (rolled is not { } result || result.Lines.Count == 0)
        {
            await AnswerAsync(ResultCannotReroll, TargetRecord());
            return;
        }

        // ---- Honing Fee (current range-rate drives the price), charged atomically ----
        long? fee = InitGrindingOptHandler.HoningFee(target.ItemId, ItemPackets.RecordAbilityValue(target), store.GrindingInfo);
        if (fee is not { } cost || !player.TryDebit(cost))
        {
            await AnswerAsync(ResultCannotReroll, TargetRecord());
            return;
        }
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold));

        // ---- consume one material (2329 clears/decrements its bag cell) ----
        (byte matBag, byte matSlot) = ItemWorkbench.ClientBagSlot(material!);
        int matRemaining = material!.Count - 1;
        if (matRemaining <= 0) { await inventory.RemoveItemAsync(materialUid); matRemaining = 0; }
        else { material.Count = matRemaining; await inventory.UpdateItemAsync(material); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, w => w.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, matSlot, matBag, materialUid,
                (byte)Math.Clamp(matRemaining, 0, byte.MaxValue), useSource: CombatPackets.UseSourceItemUse)));

        // ---- apply the roll: range-rate on record+69, the 5 lines on GrindingJson; persist ----
        target.AbilityValue = result.Grv;
        target.GrindingJson = GrindingLines.Serialize(result.Lines);
        await inventory.UpdateItemAsync(target);

        // Re-push the 2498 sheet so a honed WORN item's stats apply at once (StatService folds GrindingJson);
        // harmless for a bag item, since GetEquipmentAsync returns only worn gear. Mirrors the refine handler.
        if (player.Character is { } ch)
        {
            var worn = await inventory.GetEquipmentAsync(ch.Id);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn));
            (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, worn);
        }

        await AnswerAsync(ResultHoned, ItemPackets.BuildItemRecord126(target));

        Logger.LogInformation("{Name} honed uid {Uid} ('{Item}'): grv {Grv}, {N} lines, fee {Fee} (mat {Mat})",
            player.Name, targetUid, store.GetItem(target.ItemId)?.Name ?? "?", result.Grv, result.Lines.Count, cost, materialItem);
    }

 /// <summary>
 /// Builds a minimal item record that carries only the request's descriptor (uid, count, bag and slot)
 /// with template 0. Used for the result-3 answer.
 /// </summary>
    internal static byte[] DescriptorEcho(ulong uid, byte count, byte bag, byte slot)
    {
        var echo = new byte[ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(echo, uid);
        echo[14] = count;
        echo[15] = bag;
        echo[16] = slot;
        return echo;
    }
}
