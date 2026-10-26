using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Combines a PluriEgg Blank and a pet's material pieces into that pet's egg (RMI 1626, ReqPCollectionCombinEggBlank).</summary>
/// <remarks>
/// <para>
/// Request: <c>[u8 bag][u8 slot][u8 times][u32 PetInfo.NPC_ID]</c>. The catalyst is the slotted item. The pieces are taken from the
/// bag in the quantities the pet's data row sets, multiplied by the requested count.
/// </para>
/// <para>
/// When the recipe, the slot or the bag room is not met, nothing is taken and nothing is sent. On success the catalyst and the
/// pieces leave the bag, the material window is updated (2921) and the combine result (2924) is sent.
/// The pet is not registered here: the hatched egg still goes through the incubator and is used with 1627.
/// </para>
/// </remarks>
public class PCollectionCombinEggBlankHandler : ZoneRmiHandlerBase<PCollectionCombinEggBlankHandler>
{
    
    
    private const byte UseSourceCollection = 3;

 /// <summary>Length of the request body in bytes.</summary>
    internal const int RequestLength = 7;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionCombinEggBlank;
    public override string Name => "PCollectionCombinEggBlank";
    public PCollectionCombinEggBlankHandler(IServiceScopeFactory s, ILogger<PCollectionCombinEggBlankHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest(payload.Span, out byte bag, out byte slot, out byte times, out uint petId))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPCollectionCombinEggBlank ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        var pet = MonsterCollectionRules.Pet(store, petId);
        if (pet is null || times == 0 || pet.Pet_Egg_ID == 0 || pet.ItemID_Catalyst == 0)
        {
            Logger.LogWarning("{Name}: 1626 x{Times} for pet {Pet}, which has no usable PetInfo recipe", player.Name, times, petId);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var bagItems = await inventory.GetByCharacterAsync(player.CharacterId);

        
        int flat = BagSlotToFlat(bag, slot);
        int catalystNeed = times * Math.Max(1, (int)pet.Num_Catalyst);
        var catalyst = flat < 0 ? null : bagItems.FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        if (catalyst is null || catalyst.ItemId != pet.ItemID_Catalyst || catalyst.Count < catalystNeed)
        {
            Logger.LogWarning("{Name}: 1626 bag {Bag} slot {Slot} does not hold {Need}x catalyst {Catalyst} for pet {Pet} (found {Item} x{Count})",
                player.Name, bag, slot, catalystNeed, pet.ItemID_Catalyst, petId, catalyst?.ItemId, catalyst?.Count);
            return;
        }

        var pieceNeeds = PieceNeeds(pet, times);
        foreach (var (id, need) in pieceNeeds)
        {
            long held = BagStacks(bagItems, id).Sum(i => (long)i.Count);
            if (held < need)
            {
                Logger.LogWarning("{Name}: 1626 x{Times} for pet {Pet} needs {Need}x piece {Piece}, bag holds {Held}",
                    player.Name, times, petId, need, id, held);
                return;
            }
        }

        
        
        
        
        
        int stackMax = Math.Max(1, (int)(store.GetItem(pet.Pet_Egg_ID)?.Stack_Max ?? 1));
        var placed = new List<InventoryItem>();
        var topUp = stackMax > 1
            ? BagStacks(bagItems, pet.Pet_Egg_ID).FirstOrDefault(i => i.Count + times <= stackMax)
            : null;
        if (topUp is null)
        {
            int stacksNeeded = (times + stackMax - 1) / stackMax;
            var freeCells = BagCapacity.FreeCells(bagItems, store.Items);
            if (freeCells.Count < stacksNeeded)
            {
                Logger.LogWarning("{Name}: 1626 x{Times} for pet {Pet}: no free bag cell for egg {Egg}",
                    player.Name, times, petId, pet.Pet_Egg_ID);
                return;
            }
            for (int remaining = times, k = 0; remaining > 0; k++)
            {
                int n = Math.Min(stackMax, remaining);
                var stack = new InventoryItem
                {
                    CharacterId = player.CharacterId,
                    ItemId = pet.Pet_Egg_ID,
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

        
        int catalystLeft = catalyst.Count - catalystNeed;
        if (catalystLeft <= 0) { await inventory.RemoveItemAsync(catalyst.Id); catalystLeft = 0; }
        else { catalyst.Count = catalystLeft; await inventory.UpdateItemAsync(catalyst); }

        
        
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

        Logger.LogInformation("{Name} combines {Times}x egg {Egg} for pet {Pet} (catalyst left {Left})",
            player.Name, times, pet.Pet_Egg_ID, petId, catalystLeft);

        
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, catalyst.Id,
                (byte)Math.Clamp(catalystLeft, 0, 255), UseSourceCollection)));

        
        foreach (var stack in placed)
        {
            var body2333 = LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(stack), resultCode: 0, pickupKind: 0);
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, writer => writer.WriteBytes(body2333));
        }

        
        
        
        var book = await CollectionService.From(services, Logger).GetStateAsync(player.CharacterId, ct);
        var (state, pieces) = await MonsterCollectionRules.PetRecordAsync(
            store, inventory, player.CharacterId, petId, book.LoyaltyOf(petId));
        var nfy = CollectionPackets.BuildNfyPCollectionSelectedSkill(state, pieces);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyPCollectionSelectedSkill, writer => writer.WriteBytes(nfy));

        
        
        var ans = CollectionPackets.BuildCollectionIdResultAck(petId, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionCombinEggBlank, writer => writer.WriteBytes(ans));
    }

 /// <summary>Parses the 7-byte request body. Shorter bodies are refused; bytes past the seventh are ignored.</summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out byte bag, out byte slot, out byte times, out uint petId)
    {
        bag = slot = times = 0;
        petId = 0;
        if (body.Length < RequestLength) return false;
        bag = body[0];
        slot = body[1];
        times = body[2];
        petId = BinaryPrimitives.ReadUInt32LittleEndian(body[3..]);
        return true;
    }

 /// <summary>Returns the pieces that <paramref name="times"/> combines take, grouped by item id.</summary>
    internal static List<(uint Id, int Need)> PieceNeeds(PetInfo pet, int times) =>
        new[]
            {
                (Id: pet.ItemID_Piece1, Need: times * (int)pet.Num_Piece1),
                (Id: pet.ItemID_Piece2, Need: times * (int)pet.Num_Piece2),
                (Id: pet.ItemID_Piece3, Need: times * (int)pet.Num_Piece3),
                (Id: pet.ItemID_Piece4, Need: times * (int)pet.Num_Piece4),
            }
            .Where(p => p.Id != 0 && p.Need > 0)
            .GroupBy(p => p.Id)
            .Select(g => (Id: g.Key, Need: g.Sum(p => p.Need)))
            .ToList();

    private static IEnumerable<InventoryItem> BagStacks(IEnumerable<InventoryItem> bag, uint itemId) =>
        bag.Where(i => i.StorageType == 0 && i.ItemId == itemId && i.Count > 0).OrderBy(i => i.Slot);

 /// <summary>Maps a client (bag, slot) pair to a flat bag slot, or -1 when the pair is not a bag cell.</summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }
}
