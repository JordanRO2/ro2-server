using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles the use of a hatched pet egg on the pet collection (RMI 1627, ReqPCollectionUsePetEgg).</summary>
/// <remarks>
/// <para>
/// Request: the egg's bag cell, a window byte and the NPC id of the pet named by the egg's data row.
/// </para>
/// <para>
/// A pet not yet in the book is registered and the egg is taken. The answer order is: the egg leaves the cell (2329),
/// the pet record (2920), the active pet when it applies (2918), the book size (2919), then the result (2925).
/// </para>
/// <para>
/// A pet already in the book is fed instead: its loyalty rises by <see cref="PetCollectionBook.LoyaltyPerFeed"/>, up to the
/// pet's loyalty cap. Refusals are answered with the 2925 result alone and take no egg. A pet the pet data does not know is not answered.
/// </para>
/// </remarks>
public class PCollectionUsePetEggHandler : ZoneRmiHandlerBase<PCollectionUsePetEggHandler>
{
    
    
    
    private const byte UseSourceCollection = 3;

 /// <summary>2925 flag for a pet the egg added to the book.</summary>
    private const byte FlagAdded = 1;

 /// <summary>2925 flag for an egg fed to a pet already in the book.</summary>
    private const byte FlagFed = 2;

 /// <summary>2925 flag of a refusal: no egg taken, the result says why.</summary>
    private const byte FlagRefused = 0;

 /// <summary>2925 result: the fed pet is already at its loyalty cap, so no egg is taken.</summary>
    internal const ushort ResultLoyaltyCapped = 30001;

 /// <summary>2925 result: the named cell no longer holds the pet's hatched egg.</summary>
    internal const ushort ResultNoEgg = 30004;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionUsePetEgg;
    public override string Name => "PCollectionUsePetEgg";
    public PCollectionUsePetEggHandler(IServiceScopeFactory s, ILogger<PCollectionUsePetEggHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 7) return;
        byte bag = payload.Span[0], slot = payload.Span[1];
        uint petId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(3));

        var store = services.GetRequiredService<GameDataStore>();
        var pet = MonsterCollectionRules.Pet(store, petId);
        if (pet is null)
        {
            Logger.LogWarning("{Name}: 1627 for pet {Pet}, which has no PetInfo row", player.Name, petId);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        int flat = BagSlotToFlat(bag, slot);
        var egg = flat < 0 ? null
            : (await inventory.GetByCharacterAsync(player.CharacterId))
                .FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        if (egg is null || egg.ItemId != pet.Hatched_Egg_ID || egg.Count <= 0)
        {
            Logger.LogWarning("{Name}: 1627 bag {Bag} slot {Slot} does not hold hatched egg {Egg} of pet {Pet} (found {Item}); refused with {Code}",
                player.Name, bag, slot, pet.Hatched_Egg_ID, petId, egg?.ItemId, ResultNoEgg);
            await SendRefusalAsync(session, petId, ResultNoEgg);
            return;
        }

        var svc = CollectionService.From(services, Logger);
        var book = await svc.GetStateAsync(player.CharacterId, ct);
        if (book.RegisteredPetIds.Contains(petId))
        {
            await FeedAsync(session, player, svc, book, store, inventory, egg, bag, slot, petId, ct);
            return;
        }

        int registered = await svc.RegisterPetAsync(player.CharacterId, petId, ct);

        int remaining = await TakeOneEggAsync(inventory, egg);
        Logger.LogInformation("{Name} hatched pet {Pet} from egg {Egg} ({N} pets registered)",
            player.Name, petId, egg.ItemId, registered);

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, egg.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceCollection)));
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyPCollectionSkill, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyPCollectionSkill(PetCollectionBook.State(store, petId, 0))));
        await session.SendRmiAsync(ZoneS2COpcodes.NfyPCollection, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyPCollection(registered)));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionUsePetEgg, writer => writer.WriteBytes(
            CollectionPackets.BuildAnsPCollectionUsePetEgg(petId, FlagAdded, 0)));
    }

 /// <summary>
 /// Feeds the egg to <paramref name="petId"/>, which is already in <paramref name="book"/>: loyalty rises up to the pet's cap,
 /// one egg is taken, and the feed answer is sent. At the cap nothing is taken and the refusal is sent instead.
 /// </summary>
    private async Task FeedAsync(ProudSession session, PlayerState player, CollectionService svc,
        CollectionDocument book, GameDataStore store, IInventoryRepository inventory, InventoryItem egg,
        byte bag, byte slot, uint petId, CancellationToken ct)
    {
        
        
        if (await svc.FeedPetAsync(player.CharacterId, petId, ct) is not { } loyalty)
        {
            Logger.LogInformation("{Name}: 1627 egg {Egg} for pet {Pet}, already at loyalty {Loyalty} (its cap); refused with {Code}",
                player.Name, egg.ItemId, petId, book.LoyaltyOf(petId), ResultLoyaltyCapped);
            await SendRefusalAsync(session, petId, ResultLoyaltyCapped);
            return;
        }

        int remaining = await TakeOneEggAsync(inventory, egg);
        var record = PetCollectionBook.State(store, petId, loyalty);
        Logger.LogInformation("{Name} fed egg {Egg} to pet {Pet}: loyalty {Loyalty}, level {Level}",
            player.Name, egg.ItemId, petId, loyalty, record.LoyaltyLevel);

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, egg.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceCollection)));
        await session.SendRmiAsync(ZoneS2COpcodes.NfyPCollectionSkill, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyPCollectionSkill(record)));
        
        
        if (book.ActivePetId == petId)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionSetActivePet, writer => writer.WriteBytes(
                CollectionPackets.BuildAnsPCollectionSetActivePet(petId, record.LoyaltyLevel, 0)));
        }
        await session.SendRmiAsync(ZoneS2COpcodes.NfyPCollection, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyPCollection(book.RegisteredPetIds.Count)));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionUsePetEgg, writer => writer.WriteBytes(
            CollectionPackets.BuildAnsPCollectionUsePetEgg(petId, FlagFed, 0)));
    }

 /// <summary>Sends the refusal 2925 <c>{petId, 0, result}</c> alone. No egg is taken.</summary>
    private static Task SendRefusalAsync(ProudSession session, uint petId, ushort result)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionUsePetEgg, writer => writer.WriteBytes(
            CollectionPackets.BuildAnsPCollectionUsePetEgg(petId, FlagRefused, result)));

 /// <summary>Takes one egg from the stack and returns the count left in the cell (0 clears it).</summary>
    private static async Task<int> TakeOneEggAsync(IInventoryRepository inventory, InventoryItem egg)
    {
        int remaining = egg.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(egg.Id); remaining = 0; }
        else { egg.Count = remaining; await inventory.UpdateItemAsync(egg); }
        return remaining;
    }

 /// <summary>Maps a client (bag, slot) pair to a flat bag slot, or -1 when the pair is not a bag cell.</summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }
}
