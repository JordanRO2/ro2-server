using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqInitGrindingOpt: resets an item's grinding options with a Grinding_Reset_Item voucher. Answered with AnsInitGrindingOpt (3013).</summary>
/// <remarks>
/// <para>
/// The request body is 20 bytes, without envelope: <c>[u64 voucher item uid][u64 target item uid][u32 quoted zeny price]</c>. The price is recomputed on the server from ItemInfo and Grinding_Info data; the quoted value is only logged.
/// </para>
/// <para>
/// On success the zeny is debited (2435 OnZenyChange), one voucher is consumed (2329 OnUseItem), and 3013 is answered with result 0 and the target's 126-byte item record. This server stores no grinding options, so the record already carries none.
/// </para>
/// <para>
/// Refusals answer with a zeroed record and charge nothing: 25000 for an invalid voucher or target, or an item that cannot be priced; 25006 when the player has less zeny than the price.
/// </para>
/// <para>
/// The price uses the same formula the client shows (<see cref="ResetPrice"/>).
/// </para>
/// </remarks>
public class InitGrindingOptHandler : ZoneRmiHandlerBase<InitGrindingOptHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqInitGrindingOpt;
    public override string Name => "InitGrindingOpt";

 /// <summary>
 /// Expected request body length, in bytes.
 /// </summary>
    private const int RequestBodyLength = 20;

 /// <summary>
 /// 3013 refusal code for an invalid voucher or target, or an item that cannot be priced.
 /// </summary>
    internal const ushort InitGrindingOptInvalid = 25000;

 /// <summary>
 /// 3013 refusal code when the player has less zeny than the price.
 /// </summary>
    internal const ushort InitGrindingOptNoZeny = 25006;

 /// <summary>
 /// Offset of the item's ability value in the 126-byte item record.
 /// </summary>
    internal const int AbilityValueOffset = 69;

 /// <summary>
 /// useSource of the voucher's 2329 OnUseItem.
 /// </summary>
    private const byte UseSourceGrindingReset = 1;

    public InitGrindingOptHandler(IServiceScopeFactory s, ILogger<InitGrindingOptHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: InitGrindingOpt body is {Len} bytes, not the 20 retail sends",
                session.SessionId, payload.Length);
            return;
        }

        var span = payload.Span;
        ulong materialUid = BinaryPrimitives.ReadUInt64LittleEndian(span);
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8));
        uint quotedPrice = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        var material = materialUid != 0 && materialUid != targetUid ? await inventory.GetByIdAsync(materialUid) : null;

        if (target is null || target.CharacterId != player.CharacterId)
        {
            await RefuseAsync(session, InitGrindingOptInvalid, ct);
            Logger.LogInformation("{Name}: 1681 on uid {Uid}, which is not theirs; refused {Code}", player.Name, targetUid, InitGrindingOptInvalid);
            return;
        }
        var resetRow = material is not null && material.CharacterId == player.CharacterId && material.Count >= 1
            ? FindResetItem(store, material.ItemId) : null;
        if (resetRow is null)
        {
            await RefuseAsync(session, InitGrindingOptInvalid, ct);
            Logger.LogInformation("{Name}: 1681 on uid {Uid} with material uid {Mat} ({Tpl}), not an owned Grinding_Reset_Item stack; refused {Code}",
                player.Name, targetUid, materialUid, material?.ItemId ?? 0, InitGrindingOptInvalid);
            return;
        }
        var voucher = material!;   

        
        var record = WorldEntryService.BuildItemRecord126(target);
        uint abilityValue = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(AbilityValueOffset));
        long? price = ItemPricing.TryGetValue(target.ItemId, out var pricing)
            ? ResetPrice(pricing.AbiliVal, pricing.ItemValueLv, abilityValue, store.GrindingInfo, resetRow.DISC_RATE)
            : null;
        if (price is not { } cost)
        {
            await RefuseAsync(session, InitGrindingOptInvalid, ct);
            Logger.LogInformation("{Name}: 1681 on uid {Uid} (item {Tpl}) cannot be priced (no ItemInfo Abili_val); refused {Code}",
                player.Name, targetUid, target.ItemId, InitGrindingOptInvalid);
            return;
        }
        if (!player.TryDebit(cost))   
        {
            await RefuseAsync(session, InitGrindingOptNoZeny, ct);
            Logger.LogInformation("{Name}: 1681 needs {Cost} zeny, has {Gold}; refused {Code}", player.Name, cost, player.Gold, InitGrindingOptNoZeny);
            return;
        }

        
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold));

        var (matBag, matSlot) = ItemWorkbench.ClientBagSlot(voucher);
        int remaining = voucher.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(voucher.Id); remaining = 0; }
        else { voucher.Count = remaining; await inventory.UpdateItemAsync(voucher); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, matSlot, matBag, voucher.Id,
                (byte)Math.Clamp(remaining, 0, 255), useSource: UseSourceGrindingReset)), ct);

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsInitGrindingOpt, writer =>
        {
            writer.WriteUInt16(0);        
            writer.WriteBytes(record);    
        }, ct);

        Logger.LogInformation("{Name} reset the grinding options of uid {Uid} ('{Item}') with voucher {Mat}: {Cost} zeny (client quoted {Quote}), {Left} vouchers left",
            player.Name, targetUid, store.GetItem(target.ItemId)?.Name ?? "?", voucher.ItemId, cost, quotedPrice, remaining);
    }

 /// <summary>
 /// A refusal: the result code over a zeroed record, and nothing charged.
 /// </summary>
    private static Task RefuseAsync(ProudSession session, ushort code, CancellationToken ct)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsInitGrindingOpt, writer =>
        {
            writer.WriteUInt16(code);
            writer.WriteZeros(ItemPackets.ItemRecordLength);
        }, ct);

    private static GrindingResetItem? FindResetItem(GameDataStore store, uint templateId)
    {
        foreach (var r in store.GrindingResetItem)
            if (r.Item_ID == templateId) return r;
        return null;
    }

 /// <summary>
 /// Computes the zeny price of a grinding reset, using the same formula as the client.
 /// </summary>
 /// <param name="abiliVal">ItemInfo.Abili_val of the target.</param>
 /// <param name="itemValueLv">ItemInfo.ItemValueLv of the target.</param>
 /// <param name="abilityValue">Ability value of the target item record.</param>
 /// <param name="grindingInfo">Grinding_Info rows.</param>
 /// <param name="discRate">Grinding_Reset_Item.DISC_RATE of the voucher.</param>
 /// <remarks>
 /// Returns null when the price cannot be computed, and 0 when the gross value is not positive.
 /// </remarks>
    internal static long? ResetPrice(int abiliVal, int itemValueLv, uint abilityValue,
        IReadOnlyList<GrindingInfo> grindingInfo, ushort discRate)
    {
        if (abiliVal == 0 || grindingInfo.Count == 0) return null;
        int rate = (int)((double)(int)abilityValue / (float)abiliVal * 10000.0 - 10000.0);

        var rows = grindingInfo.OrderBy(r => r.INDEX).ToArray();
        int gold = 0;
        for (int index = 1; index <= rows.Length; index++)
        {
            var row = rows.FirstOrDefault(r => r.INDEX >= index);   
            if (row is null) return null;
            if (row.Range_Rate >= rate) { gold = row.Reset_Gold; break; }
            if (index == rows.Length) gold = row.Reset_Gold;
        }

        int gross = unchecked(gold * itemValueLv);
        float grossF = gross;
        if (grossF <= 0f) return 0;
        return (long)(int)(grossF - (double)discRate * grossF / 10000.0);
    }

 /// <summary>
 /// ItemInfo Abili_val and ItemValueLv: the two price inputs of a grinding target.
 /// </summary>
    internal sealed record GrindingPricing(int AbiliVal, int ItemValueLv);

 /// <summary>
 /// ItemInfo columns that the item data model does not carry. Loaded once from the data directory.
 /// </summary>
    private sealed record ItemPricingRow(uint ID, int Abili_val, int ItemValueLv);

    private static readonly Lazy<IReadOnlyDictionary<uint, GrindingPricing>> PricingTable = new(LoadPricing);

    private static IReadOnlyDictionary<uint, GrindingPricing> ItemPricing => PricingTable.Value;

    private static IReadOnlyDictionary<uint, GrindingPricing> LoadPricing()
    {
        try
        {
            var path = Path.Combine("data", "ItemInfo.json");
            if (!File.Exists(path)) return new Dictionary<uint, GrindingPricing>();
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<ItemPricingRow>>(File.ReadAllBytes(path));
            var byId = new Dictionary<uint, GrindingPricing>();
            foreach (var r in rows ?? new List<ItemPricingRow>())
                if (r.Abili_val != 0) byId.TryAdd(r.ID, new GrindingPricing(r.Abili_val, r.ItemValueLv));
            return byId;
        }
        catch
        {
            return new Dictionary<uint, GrindingPricing>(); 
        }
    }
}
