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

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1702 ReqGiveEffectToItem - put an FX-enchant effect item's visual effect on an equipment item</summary>
/// <remarks>
/// <para>
/// REQUEST, NONE family, <c>Blob&lt;24&gt;</c> (24 bytes), sent by the effect-enchant window:
/// <c>[u32 FX NPC actor handle@0][u32 fxIndex@4][u64 targetUid@8][u64 effectItemUid@16]</c>.
/// <list type="bullet">
/// <item>@0 is the handle of the FX NPC the window was opened at. It is not echoed back.</item>
/// <item>@4 is the FXenchant_INFO row <c>index</c>; the row names the effect item (Item_ID) and the
/// equipment template it applies to (Taget_Item_ID).</item>
/// <item>@8 is the target equipment and @16 the effect item, which the 2329 after the request uses up.</item>
/// </list>
/// </para>
/// <para>
/// ANSWER, one tick, in this order: 2435 OnZenyChange with the balance after
/// the fee, 2329 OnUseItem on the effect item (count after, useSource 3), then 3039, a raw <c>Blob&lt;22&gt;</c>
/// <c>[u16 result][u32 fxIndex][u64 targetUid][u64 effectItemUid]</c> with the request fields echoed.
/// </para>
/// <para>
/// THE FEE is 1000 x the target's ItemInfo <c>Item_Lv</c>, which matches the cost the client's window quotes:
/// it multiplies the target's Item_Lv (the value the item tooltip prints as "Item Lv") by 1000. For example,
/// a target of Item_Lv 105 costs 105,000. ItemData has no Item_Lv, so it is read once from
/// data/ItemInfo.json (<see cref="ItemLevels"/>). A 0-level target is not expected.
/// </para>
/// <para>
/// WHERE THE EFFECT GOES: the target's own record, not the answer. The client shows a result message
/// for a non-zero 3039 result; for 0, the enchant window plays the success sound and
/// resets its slots. It never touches the item: the effect reaches the client through later item records (record+113,
/// WorldEntryService.WriteItemInstance) and, for other players, the 28-byte worn records (+23,
/// EquipViewNotifyPackets.WriteEquipRecord28). So the index is stored on the item
/// (InventoryItem.FxEnchantIndex) and appears in the item's next record after
/// the 3039. A new effect replaces the old one.
/// </para>
/// <para>
/// A REFUSAL: a player without the fee gets result <see cref="ResultNoZeny"/> (16209). The client shows it as
/// string_ui 2308 "You don't have enough Zeny." A request whose row or items do not match gets no answer.
/// </para>
/// </remarks>
public class GiveEffectToItemHandler : ZoneRmiHandlerBase<GiveEffectToItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGiveEffectToItem;
    public override string Name => "GiveEffectToItem";
    public GiveEffectToItemHandler(IServiceScopeFactory s, ILogger<GiveEffectToItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    private const int RequestBytes = 24;

 /// <summary>Request offsets: <c>[u32 FX NPC handle@0][u32 fxIndex@4][u64 targetUid@8][u64 effectItemUid@16]</c>.</summary>
    private const int RequestFxIndexOffset = 4;
    private const int RequestTargetUidOffset = 8;
    private const int RequestEffectItemUidOffset = 16;

 /// <summary>3039 answer layout: <c>[u16 result@0][u32 fxIndex@2][u64 targetUid@6][u64 effectItemUid@14]</c>, 22 bytes.</summary>
    private const int AnswerBytes = 22;
    private const int AnswerFxIndexOffset = 2;
    private const int AnswerTargetUidOffset = 6;
    private const int AnswerEffectItemUidOffset = 14;

 /// <summary>InventoryItem.StorageType of a bag stack (worn equipment is 1).</summary>
    private const byte StorageTypeBag = 0;

 /// <summary>Largest item count a 2329 OnUseItem carries (u8).</summary>
    private const int MaxUseItemCount = 255;

 /// <summary>Path of the ItemInfo table under the server's data directory.</summary>
    private static readonly string ItemInfoPath = Path.Combine("data", "ItemInfo.json");

 /// <summary>3039 result 0: the effect is on the item (the only success value).</summary>
    internal const ushort ResultApplied = 0;

 /// <summary>
 /// 3039 result 16209, "You don't have enough Zeny." (string_ui 2308).
 /// </summary>
    internal const ushort ResultNoZeny = 16209;

 /// <summary>2329 useSource of the consumed effect item: always 3.</summary>
    internal const byte UseSourceFxEnchant = 3;

 /// <summary>Zeny per target Item_Lv (the cost the client quotes).</summary>
    internal const long FeePerItemLevel = 1000;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBytes) return;
        var body = payload.Span;
        uint fxIndex = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(RequestFxIndexOffset));
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(RequestTargetUidOffset));
        ulong effectItemUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(RequestEffectItemUidOffset));

        var store = services.GetRequiredService<GameDataStore>();
        var row = store.FXenchantINFO.FirstOrDefault(r => r.index == fxIndex);
        if (row is null)
        {
            Logger.LogWarning("{Name} FX enchant: no FXenchant_INFO row {Index}", player.Name, fxIndex);
            return;
        }

        // Item uids are global: ownership is checked, and each item must be the one the row names. The effect
        // item is consumed from a bag cell, so it must be a bag stack.
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var target = await inventory.GetByIdAsync(targetUid);
        var effectItem = await inventory.GetByIdAsync(effectItemUid);
        if (target is null || target.CharacterId != player.CharacterId || target.ItemId != row.Taget_Item_ID
            || effectItem is null || effectItem.CharacterId != player.CharacterId || effectItem.ItemId != row.Item_ID
            || effectItem.StorageType != StorageTypeBag || effectItem.Count < 1 || effectItemUid == targetUid)
        {
            Logger.LogWarning("{Name} FX enchant row {Index} (effect {Effect} on {Target}) does not match the items: "
                + "target {TUid} = {TItem}, effect item {EUid} = {EItem}",
                player.Name, fxIndex, row.Item_ID, row.Taget_Item_ID,
                targetUid, target is null ? "missing" : $"{target.ItemId} of char {target.CharacterId}",
                effectItemUid, effectItem is null ? "missing" : $"{effectItem.ItemId} of char {effectItem.CharacterId} (storage {effectItem.StorageType}, count {effectItem.Count})");
            return;
        }
        if (!ItemLevels.TryGetValue(target.ItemId, out int itemLevel))
        {
            Logger.LogWarning("{Name} FX enchant row {Index}: target template {Target} has no ItemInfo Item_Lv; not answered",
                player.Name, fxIndex, target.ItemId);
            return;
        }
        long fee = Fee(itemLevel);

        if (!player.TryDebit(fee))   // the fee is taken in the same atomic step that checks it
        {
            Logger.LogInformation("{Name} FX enchant row {Index} needs {Fee} zeny, has {Gold}; refused {Code}",
                player.Name, fxIndex, fee, player.Gold, ResultNoZeny);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGiveEffectToItem,
                Build3039(ResultNoZeny, fxIndex, targetUid, effectItemUid), ct);
            return;
        }

        // 1. 2435: the fee. The balance after the fee goes out, not the change.
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnZenyChange,
            ActorVitalsPackets.BuildZenyChange2435(player.EntityId, player.Gold));

        // 2. 2329: one effect item used up, with the count after and useSource 3.
        var (bag, slot) = ItemWorkbench.ClientBagSlot(effectItem);
        int left = effectItem.Count - 1;
        if (left <= 0) { await inventory.RemoveItemAsync(effectItem.Id); left = 0; }
        else { effectItem.Count = left; await inventory.UpdateItemAsync(effectItem); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, w => w.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, effectItem.Id, (byte)Math.Clamp(left, 0, MaxUseItemCount),
                useSource: UseSourceFxEnchant)), ct);

        // 3. The effect on the target: its later records carry the index at record+113 (+23 of its worn record).
        uint previous = target.FxEnchantIndex;
        target.FxEnchantIndex = row.index;
        await inventory.UpdateItemAsync(target);

        // 4. 3039, the request echoed with result 0.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGiveEffectToItem,
            Build3039(ResultApplied, fxIndex, targetUid, effectItemUid), ct);

        Logger.LogInformation("{Name} FX enchant row {Index}: effect item {Effect} (uid {EUid}, {Left} left) on {Target} "
            + "(uid {TUid}, Item_Lv {Lv}, replacing {Old}) for {Fee} zeny",
            player.Name, fxIndex, row.Item_ID, effectItemUid, left, row.Taget_Item_ID, targetUid, itemLevel, previous, fee);
    }

 /// <summary>The fee for a target of ItemInfo Item_Lv <paramref name="itemLevel"/> (see the class remarks).</summary>
    internal static long Fee(int itemLevel) => FeePerItemLevel * Math.Max(0, itemLevel);

 /// <summary>
 /// AnsGiveEffectToItem (3039), Blob&lt;22&gt;: <c>[u16 result][u32 fxIndex][u64 targetUid][u64 effectItemUid]</c>,
 /// the request's @4/@8/@16 echoed.
 /// </summary>
    internal static byte[] Build3039(ushort result, uint fxIndex, ulong targetUid, ulong effectItemUid)
    {
        var b = new byte[AnswerBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, result);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(AnswerFxIndexOffset), fxIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(AnswerTargetUidOffset), targetUid);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(AnswerEffectItemUidOffset), effectItemUid);
        return b;
    }

 /// <summary>The ItemInfo column ItemData does not carry, read once from the same "data" directory Program.cs hands
 /// GameDataStore (the pattern InitGrindingOptHandler uses for its price columns).</summary>
    private sealed record ItemLevelRow(uint ID, int Item_Lv);

    private static readonly Lazy<IReadOnlyDictionary<uint, int>> ItemLevelTable =
        new(() => LoadItemLevels(ItemInfoPath));

 /// <summary>ItemInfo <c>Item_Lv</c> by template id.</summary>
    private static IReadOnlyDictionary<uint, int> ItemLevels => ItemLevelTable.Value;

 /// <summary>ItemInfo <c>Item_Lv</c> by template id from the ItemInfo.json at <paramref name="path"/>; empty when the
 /// file is missing or unreadable (every enchant then goes unanswered rather than charged a guessed fee).</summary>
    internal static IReadOnlyDictionary<uint, int> LoadItemLevels(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<uint, int>();
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<ItemLevelRow>>(File.ReadAllBytes(path));
            var byId = new Dictionary<uint, int>();
            foreach (var r in rows ?? new List<ItemLevelRow>())
                byId.TryAdd(r.ID, r.Item_Lv);
            return byId;
        }
        catch
        {
            return new Dictionary<uint, int>();
        }
    }
}
