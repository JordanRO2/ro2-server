using System.Buffers.Binary;
using System.Collections.Generic;
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

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1456 ReqCardComposition - compose/fuse cards.
/// Request (raw VarBlob32):
/// [u32 varblob32Len][u32 npcHandle][u8 sourceIndex][u16 cntA][cntA x Blob11][u16 cntB][cntB x Blob11]
/// where Blob11 = [u8 bag][u8 slot][u64 uid][u8 qty] (same descriptor as ReqPCItemSwap; the client writes
/// the item's count less its reserved count as qty).
/// vecA = the catalysts (synthesis stone + composition ticket, or the stone alone), vecB = the cards,
/// vecB[sourceIndex] being the card that is composed. Answers with the bag changes and AckCardComposition
/// (2616), fixed 10 bytes: [u16 result][u32 sourceCardId][u32 resultCardId].</summary>
/// <remarks>
/// <para>Retail behavior:</para>
/// <list type="bullet">
/// <item>The u8 after the handle is the INDEX of the source card in vecB: the card named in a successful
/// 2616, or the one card left untouched by a failed one, is vecB[u8] (u8 = 0, 3 or 4).</item>
/// <item>Every input stack loses exactly ONE item, the source card only on success. The 2379s come in request
/// order: vecA, then vecB without the source, then the source when it was used.</item>
/// <item>Success: 2333 with the result card first (onto its existing stack, or a new
/// one), then the 2379s, then 2616 [0][source][Compose_Result(source)]. Failure: the 2379s, then 2616
/// [22871][0][0]. Refusal: the 2616 alone with no bag change (see <see cref="PlanFor"/>).</item>
/// <item>The success rate is the stone's CardCompose.Compose_Ratio (5000 for both stones). Every card rolled has
/// the stone's Card_Grade: the 4 or 5 cards the 2379s and 2616 name are of one grade.</item>
/// <item>An emptied input stack's 2379 record keeps only uid, template and cell (<see cref="EmptiedItemRecord"/>);
/// a stack that keeps a count gets its full record, with the template's durability and the bind byte
/// (Bind_Type 2 -&gt; 2, Bind_Type 0 and 1 -&gt; 0).</item>
/// <item>The result is a +0 unbound card: the success 2333 carries refine level 0 and bind 0, also when it lands on
/// an existing stack. So it joins only a +0 unbound stack of the card
/// (<see cref="CardStacks.FindJoinStack"/>); a +10 or bound stack is not topped up, which would enforce or bind the
/// new card silently.</item>
/// </list>
/// Special_Compose_Result / Special_Compose_Ratio are not applied: what triggers a special result is not known.
/// </remarks>
public class CardCompositionHandler : ZoneRmiHandlerBase<CardCompositionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardComposition;
    public override string Name => "CardComposition";
    public CardCompositionHandler(IServiceScopeFactory s, ILogger<CardCompositionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>2616 refusal: no room for the result. From the client only: ShowResultCodeMessage
 /// case 24108, shows string_ui 11015 "You don't have enough space in your inventory." No retail frame.</summary>
    internal const ushort ResultNoBagSpace = 24108;

 /// <summary>2616 refusal: an input is not an item of this bag. Case 24109 of the same switch, string_ui 11017
 /// "Item ID does not exist for the inserted item, or the item is not a Card or Card Compound Stone." Sent
 /// for a malformed input (<see cref="CardPackets.ComposeRejected"/>).</summary>
    internal const ushort ResultUnknownItem = CardPackets.ComposeRejected;

 /// <summary>2616 refusal: the inputs are items but not a valid composition. Case 24110, string_ui 11016 "Does not
 /// meet the card compound requirement. You don't have 5 cards, or the item is not a card, or you don't have a
 /// Card Compound Stone."</summary>
    internal const ushort ResultNotComposable = 24110;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();

        var request = ParseRequest(payload.Span);
        if (request is null)
        {
            Logger.LogInformation("{Name}: malformed 1456 ({Len} B), refused with {Code}", player.Name, payload.Length, ResultUnknownItem);
            await SendAckAsync(session, CardPackets.BuildCardComposition2616(ResultUnknownItem, 0, 0), ct);
            return;
        }

        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        var (plan, refusal) = PlanFor(request, owned, store);
        if (plan is null)
        {
            Logger.LogInformation("{Name}: card composition refused with {Code}", player.Name, refusal);
            await SendAckAsync(session, CardPackets.BuildCardComposition2616(refusal, 0, 0), ct);
            return;
        }

        
        
        
        
        
        
        var template = store.GetItem(plan.ResultCardId);
        var joinStack = CardStacks.FindJoinStack(owned, template, plan.ResultCardId, grade: 0, bound: false);
        short cell = -1;
        if (joinStack is null)
        {
            var free = BagCapacity.FreeCells(owned, store.Items);
            if (free.Count == 0)
            {
                Logger.LogInformation("{Name}: card composition refused with {Code}, no cell for {Result}",
                    player.Name, ResultNoBagSpace, plan.ResultCardId);
                await SendAckAsync(session, CardPackets.BuildCardComposition2616(ResultNoBagSpace, 0, 0), ct);
                return;
            }
            cell = free[0];
        }

        bool success = Random.Shared.Next(10000) < plan.Ratio;

        
        var consumed = new List<InventoryItem>(plan.Materials);
        if (success) consumed.Add(plan.Source);

        byte[]? resultRecord = null;
        if (success)
        {
            InventoryItem resultRow;
            if (joinStack is not null)
            {
                
                
                
                joinStack.Count += 1;
                await inventory.UpdateItemAsync(joinStack);
                resultRow = joinStack;
            }
            else
            {
                
                
                resultRow = new InventoryItem
                {
                    CharacterId = player.CharacterId,
                    ItemId = plan.ResultCardId,
                    Count = 1,
                    Slot = cell,
                    StorageType = 0,
                    Durability = template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue),
                };
                await inventory.AddItemAsync(resultRow);   
            }
            resultRecord = ItemPackets.BuildItemRecord126(resultRow);
        }

        var removedRecords = new List<byte[]>(consumed.Count);
        foreach (var it in consumed)
        {
            it.Count = Math.Max(it.Count, 1) - 1;   
            if (it.Count == 0)
            {
                
                removedRecords.Add(EmptiedItemRecord.Build(it));
                await inventory.RemoveItemAsync(it.Id);
            }
            else
            {
                await inventory.UpdateItemAsync(it);
                removedRecords.Add(ItemPackets.BuildItemRecord126(it));
            }
        }

        Logger.LogInformation("{Name}: card composition {Src} -> {Res} {Outcome} (ratio {Ratio}/10000, {N} stacks consumed, joined={Joined})",
            player.Name, plan.Source.ItemId, plan.ResultCardId, success ? "succeeded" : "failed", plan.Ratio, consumed.Count,
            success && joinStack is not null);

        if (resultRecord is not null)
        {
            var addBody = LootNotifyPackets.BuildPickupItem2333(resultRecord);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, writer => writer.WriteBytes(addBody), ct);
        }
        foreach (var rec in removedRecords)
        {
            var body2379 = QuestProgressPackets.BuildItemRemoved2379(player.EntityId, rec);
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, writer => writer.WriteBytes(body2379), ct);
        }

        var ack = success
            ? CardPackets.BuildCardComposition2616(0, plan.Source.ItemId, plan.ResultCardId)
            : CardPackets.BuildCardComposition2616(CardPackets.ComposeFailed, 0, 0);
        await SendAckAsync(session, ack, ct);

        
        
        
        
        
        if (resultRecord is not null)
            await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { plan.ResultCardId },
                services, Logger);
    }

    private static Task SendAckAsync(ProudSession session, byte[] ack, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AckCardComposition, writer => writer.WriteBytes(ack), ct);

 /// <summary>One Blob11 input descriptor: <c>[u8 bag][u8 slot][u64 uid][u8 qty]</c>.</summary>
    internal readonly record struct Descriptor(byte Bag, byte Slot, ulong Uid, byte Qty);

    internal sealed record Request(byte SourceIndex, List<Descriptor> Catalysts, List<Descriptor> Cards);

    internal sealed record Plan(List<InventoryItem> Materials, InventoryItem Source, uint ResultCardId, int Ratio);

 /// <summary>The cards a composition takes: every request has 5 in vecB.</summary>
    internal const int CardsPerComposition = 5;

 /// <summary>
 /// Parse the request; null when it is malformed.
 /// </summary>
    internal static Request? ParseRequest(ReadOnlySpan<byte> body)
    {
        
        if (body.Length < 9) return null;
        byte sourceIndex = body[8];
        int p = 9;
        var vecs = new List<Descriptor>[2];
        for (int v = 0; v < 2; v++)
        {
            if (p + 2 > body.Length) return null;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(p)); p += 2;
            if (count > 64) return null;                          
            vecs[v] = new List<Descriptor>(count);
            for (int i = 0; i < count; i++)
            {
                if (p + 11 > body.Length) return null;
                vecs[v].Add(new Descriptor(body[p], body[p + 1], BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p + 2)), body[p + 10]));
                p += 11;
            }
        }
        return new Request(sourceIndex, vecs[0], vecs[1]);
    }

 /// <summary>
 /// Resolve the request against the character's rows (<paramref name="owned"/>, from GetByCharacterAsync) and the
 /// tables: the plan, or null and the 2616 refusal code. Nothing is changed either way.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The split between the two refusal codes follows the client's strings (see the constants): a request with a
 /// qty-0 descriptor gets 24109, and a request that names the same cell in vecA and vecB gets 24110. So:
 /// </para>
 /// <list type="bullet">
 /// <item>24109: a uid that is not a bag row (StorageType 0) of this character, or a descriptor with qty 0.</item>
 /// <item>24110: the same uid twice, a vecB count other than 5 or a source index outside it, a card not in CardInfo,
 /// no CardCompose stone in vecA, a card of another grade than the stone's Card_Grade (or, for a Card_Grade 0
 /// stone, than the source's), or a source with no Compose_Result.</item>
 /// </list>
 /// PlanFor accepts every well-formed request: vecA of the stone and one other item, or of the stone alone.
 /// </remarks>
    internal static (Plan? Plan, ushort Refusal) PlanFor(Request req, IReadOnlyList<InventoryItem> owned, GameDataStore store)
    {
        var bagRows = new Dictionary<ulong, InventoryItem>();
        foreach (var it in owned)
            if (it.StorageType == 0) bagRows.TryAdd(it.Id, it);

        var seen = new HashSet<ulong>();
        bool duplicate = false;
        List<InventoryItem>? Resolve(List<Descriptor> descriptors)
        {
            var rows = new List<InventoryItem>(descriptors.Count);
            foreach (var d in descriptors)
            {
                if (d.Qty == 0 || d.Uid == 0 || !bagRows.TryGetValue(d.Uid, out var row)) return null;
                if (!seen.Add(d.Uid)) duplicate = true;
                rows.Add(row);
            }
            return rows;
        }

        var catalysts = Resolve(req.Catalysts);
        var cards = catalysts is null ? null : Resolve(req.Cards);
        if (catalysts is null || cards is null) return (null, ResultUnknownItem);
        if (duplicate) return (null, ResultNotComposable);

        if (cards.Count != CardsPerComposition || req.SourceIndex >= cards.Count) return (null, ResultNotComposable);

        var cardInfo = new Dictionary<uint, RO2.GameData.Tables.CardInfo>();
        foreach (var c in store.CardInfo) cardInfo.TryAdd(c.Card_ID, c);
        foreach (var it in cards)
            if (!cardInfo.ContainsKey(it.ItemId)) return (null, ResultNotComposable);

        RO2.GameData.Tables.CardCompose? stone = null;
        foreach (var it in catalysts)
        {
            foreach (var row in store.CardCompose)
                if (row.ItemID == it.ItemId) { stone = row; break; }
            if (stone is not null) break;
        }
        if (stone is null) return (null, ResultNotComposable);

        var source = cards[req.SourceIndex];
        var sourceInfo = cardInfo[source.ItemId];
        byte grade = stone.Card_Grade != 0 ? stone.Card_Grade : sourceInfo.Grade_Type;
        foreach (var it in cards)
            if (cardInfo[it.ItemId].Grade_Type != grade) return (null, ResultNotComposable);
        if (sourceInfo.Compose_Result == 0) return (null, ResultNotComposable);

        var materials = new List<InventoryItem>(catalysts.Count + cards.Count - 1);
        materials.AddRange(catalysts);
        for (int i = 0; i < cards.Count; i++)
            if (i != req.SourceIndex) materials.Add(cards[i]);

        return (new Plan(materials, source, sourceInfo.Compose_Result, stone.Compose_Ratio), 0);
    }
}
