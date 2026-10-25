using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1693 (ReqMakeOfferings): offers a stack of items to a world-event NPC.
/// </summary>
/// <remarks>
/// <para>
/// Body: a VarBlob32 with <c>[u32 innerLen=13][u32 npcHandle][u64 itemUid][u8 count]</c>. The target is an NPC
/// handle. The event NPCs are the Type_Data_1 NPCs of WorldEvent_Check_Group.
/// </para>
/// <para>
/// Reply: 2329 OnUseItem for the offered item (result 0, use source 3, stack reduced by <c>count</c>), then 3029
/// AnsMakeOfferings. Any item can be offered, equipment and consumables alike. A 2330 OnUseItemNotify for the
/// offered item may arrive between the two.
/// </para>
/// <para>
/// Event progress: once 3029 is sent, WorldEventService counts the offering when the (NPC template, item template)
/// pair matches a row of the current stage's Check_Group_ID_1. When every row of the group reaches its
/// Type_Data_3, the event moves to its Next_Stage and every online player receives the 3030 stage push right after
/// the completing 3029. An offering that matches no current row is still consumed and acknowledged.
/// </para>
/// <para>
/// A target that is not an NPC is answered with 3029 and error 20055. A request that fails the ownership or stack
/// check is dropped silently.
/// </para>
/// <para>
/// Message order: 2577, one 2428 per held collect quest of the item, 2329, 2330, 3029, 3030.
/// </para>
/// </remarks>
public class MakeOfferingsHandler : ZoneRmiHandlerBase<MakeOfferingsHandler>
{
 /// <summary>Inner length of the body blob, always 13.</summary>
    private const int InnerLength = 13;

 /// <summary>Use source in the 2329 reply for an offering (3; a plain item use carries 1).</summary>
    private const byte UseSourceOffering = 3;

 /// <summary>WorldEvent_Check_Group.Check_Type of the "offer item X to NPC Y" rows.</summary>
    private const byte CheckTypeOffering = 2;

    public override ushort Opcode => ZoneC2SOpcodes.ReqMakeOfferings;
    public override string Name => "MakeOfferings";

    public MakeOfferingsHandler(IServiceScopeFactory s, ILogger<MakeOfferingsHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4 + InnerLength) return;
        var innerLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        if (innerLen < InnerLength) return;
        var blob = payload.Span.Slice(4);
        var npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        var itemUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(4));
        var count = blob[12];
        if (count == 0) return;

        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle);
        if (npc is null)
        {
            
            Logger.LogInformation("{Name}: 1693 offering to handle {Handle:X8}, which is no NPC here; refused with {Code}",
                player.Name, npcHandle, EconomyMiscPackets.MakeOfferingsNotUsableNpc);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsMakeOfferings, writer => writer.WriteBytes(
                EconomyMiscPackets.BuildMakeOfferingsAck(0, EconomyMiscPackets.MakeOfferingsNotUsableNpc)), ct);
            return;
        }

        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var item = await inventory.GetByIdAsync(itemUid);
        if (item is null || item.CharacterId != player.CharacterId || item.StorageType != 0 || item.Count < count)
        {
            Logger.LogWarning("{Name}: 1693 offering of uid {Uid} x{Count} refused (item {Item}, stack {Stack}, storage {Storage})",
                player.Name, itemUid, count, item?.ItemId, item?.Count, item?.StorageType);
            return;
        }

        
        
        var flat = item.Slot < 0 ? 0 : (int)item.Slot;
        byte bagIx, slotIx;
        if (flat < 20) { bagIx = 1; slotIx = (byte)(50 + flat); }
        else { var i = flat - 20; bagIx = (byte)Math.Clamp(2 + i / 35, 2, 4); slotIx = (byte)(i % 35); }

        var remaining = item.Count - count;
        if (remaining <= 0) { await inventory.RemoveItemAsync(item.Id); remaining = 0; }
        else { item.Count = remaining; await inventory.UpdateItemAsync(item); }

        var store = services.GetRequiredService<GameDataStore>();
        var row = store.WorldEventCheckGroup.FirstOrDefault(r =>
            r.Check_Type == CheckTypeOffering && r.Type_Data_1 == npc.NpcId && r.Type_Data_2 == item.ItemId);
        ushort messageStringId = row is null ? (ushort)0 : row.Type_Data_4;

        Logger.LogInformation("{Name} offers {Count}x item {Item} to NPC {Npc} (left {Left}, message {Msg})",
            player.Name, count, item.ItemId, npc.NpcId, remaining, messageStringId);

        
        
        await KharaService.OnHeldItemChangedAsync(session, player, item.ItemId, services, Logger, ct);
        await QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, item.ItemId, services, Logger, ct);

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slotIx, bagIx, item.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceOffering)));

        if (store.GetItem(item.ItemId) is { Skill_ID_1: not 0 })
            await session.SendRmiAsync(ZoneS2COpcodes.OnUseItemNotify, writer => writer.WriteBytes(
                LootNotifyPackets.BuildUseItemNotify2330(player.EntityId, item.ItemId)));

        await session.SendRmiAsync(ZoneS2COpcodes.AnsMakeOfferings,
            writer => writer.WriteBytes(EconomyMiscPackets.BuildMakeOfferingsAck(messageStringId, 0)));

        
        
        await services.GetRequiredService<WorldEventService>().OnOfferingAsync(npc.NpcId, item.ItemId, count, ct);
    }
}
