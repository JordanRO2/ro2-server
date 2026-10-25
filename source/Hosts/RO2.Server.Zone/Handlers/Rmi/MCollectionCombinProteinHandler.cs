using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1622 (ReqMCollectionCombinProtein): combines a monster collection's four DNA pieces and a
/// DNA binding protein into the monster's DNA item.
/// </summary>
/// <remarks>
/// Replies with 2329, 2333, 2915 and 2922, in that order.
/// </remarks>
public class MCollectionCombinProteinHandler : ZoneRmiHandlerBase<MCollectionCombinProteinHandler>
{
    
    
    private const byte UseSourceCollection = 3;

    public override ushort Opcode => ZoneC2SOpcodes.ReqMCollectionCombinProtein;
    public override string Name => "MCollectionCombinProtein";
    public MCollectionCombinProteinHandler(IServiceScopeFactory s, ILogger<MCollectionCombinProteinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 7) return;
        
        
        
        
        
        
        
        
        
        
        var span = payload.Span;
        byte bag = span[0], slot = span[1], times = span[2];
        uint collectionId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(3));

        
        
        var store = services.GetRequiredService<GameDataStore>();
        var recipe = MonsterCollectionRules.Row(store, collectionId, 1);
        if (recipe is null || times == 0 || recipe.ItemID_Result == 0)
        {
            Logger.LogWarning("{Name}: 1622 x{Times} for unknown monster collection {Id}", player.Name, times, collectionId);
            return;
        }

        
        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var bagItems = await inventory.GetByCharacterAsync(player.CharacterId);

        int flat = BagSlotToFlat(bag, slot);
        int proteinNeed = times * Math.Max(1, (int)recipe.Num_Combine1);
        var protein = flat < 0 ? null : bagItems.FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        if (protein is null || protein.ItemId != recipe.ItemID_Combine1 || protein.Count < proteinNeed)
        {
            Logger.LogWarning("{Name}: 1622 bag {Bag} slot {Slot} does not hold {Need}x protein {Protein} for collection {Id} (found {Item} x{Count})",
                player.Name, bag, slot, proteinNeed, recipe.ItemID_Combine1, collectionId, protein?.ItemId, protein?.Count);
            return;
        }

        
        
        
        var pieceNeeds = new[]
            {
                (Id: recipe.ItemID_Piece1, Need: times * (int)recipe.Num_Piece1),
                (Id: recipe.ItemID_Piece2, Need: times * (int)recipe.Num_Piece2),
                (Id: recipe.ItemID_Piece3, Need: times * (int)recipe.Num_Piece3),
                (Id: recipe.ItemID_Piece4, Need: times * (int)recipe.Num_Piece4),
            }
            .Where(p => p.Id != 0 && p.Need > 0)
            .GroupBy(p => p.Id)
            .Select(g => (Id: g.Key, Need: g.Sum(p => p.Need)))
            .ToList();
        foreach (var (id, need) in pieceNeeds)
        {
            long held = BagStacks(bagItems, id).Sum(i => (long)i.Count);
            if (held < need)
            {
                Logger.LogWarning("{Name}: 1622 x{Times} for collection {Id} needs {Need}x piece {Piece}, bag holds {Held}",
                    player.Name, times, collectionId, need, id, held);
                return;
            }
        }

        
        
        
        
        
        
        
        
        
        int stackMax = Math.Max(1, (int)(store.GetItem(recipe.ItemID_Result)?.Stack_Max ?? 1));
        var placed = new List<InventoryItem>();
        var topUp = stackMax > 1
            ? BagStacks(bagItems, recipe.ItemID_Result).FirstOrDefault(i => i.Count + times <= stackMax)
            : null;
        if (topUp is null)
        {
            int stacksNeeded = (times + stackMax - 1) / stackMax;
            var freeCells = BagCapacity.FreeCells(bagItems, store.Items);
            if (freeCells.Count < stacksNeeded)
            {
                Logger.LogWarning("{Name}: 1622 x{Times} for collection {Id}: no free bag cell for DNA {Dna}",
                    player.Name, times, collectionId, recipe.ItemID_Result);
                return;
            }
            for (int remaining = times, k = 0; remaining > 0; k++)
            {
                int n = Math.Min(stackMax, remaining);
                var stack = new InventoryItem
                {
                    CharacterId = player.CharacterId,
                    ItemId = recipe.ItemID_Result,
                    Count = n,
                    Slot = freeCells[k],
                    StorageType = 0,
                };
                await inventory.AddItemAsync(stack);   
                placed.Add(stack);
                remaining -= n;
            }
        }
        else
        {
            topUp.Count += times;
            await inventory.UpdateItemAsync(topUp);
            placed.Add(topUp);
        }

        
        int proteinLeft = protein.Count - proteinNeed;
        if (proteinLeft <= 0) { await inventory.RemoveItemAsync(protein.Id); proteinLeft = 0; }
        else { protein.Count = proteinLeft; await inventory.UpdateItemAsync(protein); }

        
        
        
        
        
        
        
        
        foreach (var (id, need) in pieceNeeds)
        {
            int left = need;
            foreach (var stack in BagStacks(bagItems, id).ToList())
            {
                if (left <= 0) break;
                int take = Math.Min(left, stack.Count);
                stack.Count -= take;
                left -= take;
                if (stack.Count <= 0) await inventory.RemoveItemAsync(stack.Id);
                else await inventory.UpdateItemAsync(stack);
            }
        }

        Logger.LogInformation("{Name} combines {Times}x DNA {Dna} for collection {Id} (protein left {Left})",
            player.Name, times, recipe.ItemID_Result, collectionId, proteinLeft);

        
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, protein.Id,
                (byte)Math.Clamp(proteinLeft, 0, 255), UseSourceCollection)));

        
        
        
        foreach (var stack in placed)
        {
            var body2333 = LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack), resultCode: 0, pickupKind: 0);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, writer => writer.WriteBytes(body2333));
        }

        
        
        
        
        
        
        
        
        
        var book = await CollectionService.From(services, Logger).GetStateAsync(player.CharacterId, ct);
        bool registered = book.RegisteredMonsterIds.Contains(collectionId);
        var progress = await MonsterCollectionProgressStore.From(services, Logger).GetAsync(player.CharacterId, ct);
        var state = MonsterCollectionRules.State(store, progress, collectionId, registered);
        var pieces = await MonsterCollectionRules.MonsterPieceCountsAsync(store, inventory, player.CharacterId, collectionId);
        var nfy = CollectionPackets.BuildNfyMCollectionSelectedSkill(state, pieces);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyMCollectionSelectedSkill, writer => writer.WriteBytes(nfy));

        
        
        var ans = CollectionPackets.BuildCollectionIdResultAck(collectionId, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollectionCombinProtein, writer => writer.WriteBytes(ans));
    }

    private static IEnumerable<InventoryItem> BagStacks(IEnumerable<InventoryItem> bag, uint itemId) =>
        bag.Where(i => i.StorageType == 0 && i.ItemId == itemId && i.Count > 0).OrderBy(i => i.Slot);

 /// <summary>Maps a client (bag, slot) pair to the flat bag slot used by the item record. The main bag (container 1,
 /// slots 50-69) maps to 0-19, bags 2-4 to 20 + (bag - 2) * 35 + slot. Returns -1 otherwise.</summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }
}
