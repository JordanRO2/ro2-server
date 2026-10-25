using RO2.Economy.Domain;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1349 ReqCraftItem: crafts a batch of one recipe. The batch turns materials into the product one item at a
/// time. RMI 1350 (<see cref="CancelCraftItemHandler"/>) stops it.
/// </summary>
/// <remarks>
/// <para>
/// Request body (compact-length envelope, msgTag 58389): <c>[01 11][u16 innerSize 17][u16 msgTag][u32 crafter handle @4]
/// [u8 @8][u32 handle @9][u16 recipeId @13][u16 quantity @15]</c>, offsets counted in the blob.
/// The recipe id is a row of Craft_ItemList and Craft_Material. The quantity is the number of items to make.
/// </para>
/// <para>
/// Each tick makes one item and sends the matching updates to the crafter. The batch start and end are also sent to
/// nearby players. The batch ends when the quantity is made, the materials run out, the batch is stopped, or the
/// player leaves the zone.
/// </para>
/// <para>
/// The request is refused with a 2539 result code, sent to the crafter only, when a batch is already running, the
/// recipe is not known to the character, or the materials do not cover one item. A request with no room in the bag
/// for one tick is not started and gets no answer.
/// </para>
/// </remarks>
public class CraftItemHandler : ZoneRmiHandlerBase<CraftItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftItem;
    public override string Name => "CraftItem";

 /// <summary>Blob length of the request: 17 bytes.</summary>
    private const int BlobLength = 17;

 /// <summary>Blob offset of the u16 recipe id (a Craft_ItemList / Craft_Material row id).</summary>
    private const int RecipeOffset = 13;

 /// <summary>Blob offset of the u16 quantity: how many items to make.</summary>
    private const int QuantityOffset = 15;

 /// <summary>Inventory storage type of the main bag and the extra bags.</summary>
    private const byte StorageBag = 0;

 /// <summary>Offset of the u8 stack count in a 126-byte item record.</summary>
    private const int ItemRecordCountOffset = 14;

 /// <summary>Craft state byte sent in the 2398 state update while a batch runs.</summary>
    internal const byte CraftingState = 0x13;

 /// <summary>State byte of the 2398 update that ends a batch: back to the normal state.</summary>
    private const byte NormalState = 0;

 /// <summary>Recipe id carried by a batch end that names no recipe (a stopped batch).</summary>
    private const ushort NoRecipe = 0;

 /// <summary>Value at blob+19 of the craft 2398 state update, the same on batch start and end.</summary>
    internal const uint ChangeStateTrailer = 2;

 /// <summary>
 /// Result for a request that arrives while the session's batch is running ("Crafting conditions are not satisfied.").
 /// </summary>
    internal const short ResultBatchRunning = 22627;

 /// <summary>
 /// Result for a recipe the character has not learned, or that does not exist ("Have not learned yet.").
 /// </summary>
    internal const short ResultNotLearned = 22628;

 /// <summary>Result for materials that do not cover one item ("You don't have enough material.").</summary>
    internal const short ResultNoMaterial = 22630;

 /// <summary>One running batch per zone session.</summary>
    internal sealed class Batch
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly SemaphoreSlim Gate = new(1, 1);
        public required ushort RecipeId { get; init; }
    }

    private static readonly ConcurrentDictionary<uint, Batch> Batches = new();

    public CraftItemHandler(IServiceScopeFactory s, ILogger<CraftItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// Stops <paramref name="sessionId"/>'s running batch, if any, and waits for a tick in progress to finish, so that
 /// nothing about the batch is sent after the caller's reply. Returns false when no batch was running.
 /// </summary>
    internal static async Task<bool> StopBatchAsync(uint sessionId)
    {
        if (!Batches.TryRemove(sessionId, out var batch)) return false;
        batch.Cts.Cancel();
        await batch.Gate.WaitAsync();
        batch.Gate.Release();
        return true;
    }

 /// <summary>
 /// Claims <paramref name="sessionId"/>'s batch slot for a new batch of <paramref name="recipeId"/>, or returns null
 /// when a batch already holds it. The claim is atomic, so of two simultaneous requests only one gets the slot. The
 /// returned batch's <see cref="Batch.Gate"/> is already taken, and the caller holds it until the start is sent.
 /// </summary>
    internal static Batch? TryClaimBatch(uint sessionId, ushort recipeId)
    {
        var batch = new Batch { RecipeId = recipeId };
        if (!Batches.TryAdd(sessionId, batch)) return null;
        batch.Gate.Wait();   
        return batch;
    }

 /// <summary>
 /// Gives back a slot claimed by <see cref="TryClaimBatch"/> for a request that did not start, and releases its
 /// gate. A slot already taken by a stop request is left alone.
 /// </summary>
    internal static void ReleaseUnstarted(uint sessionId, Batch batch)
    {
        Batches.TryRemove(new KeyValuePair<uint, Batch>(sessionId, batch));
        batch.Gate.Release();
    }

 /// <summary>
 /// Returns the refusal code for a request once the batch slot is free, or null when the craft may start. Checks
 /// that the recipe is known to the character (<see cref="ResultNotLearned"/>), then that the bag covers one item
 /// (<see cref="ResultNoMaterial"/>).
 /// </summary>
    internal static short? RefusalCode(CraftItemList? recipe, CraftMaterial? materialRow,
        IReadOnlyCollection<ushort> knownRecipes, IReadOnlyList<InventoryItem> bag)
    {
        if (recipe is null || materialRow is null || recipe.Craft_Item_ID == 0) return ResultNotLearned;
        var materials = MaterialsOf(materialRow);
        if (materials.Count == 0 || !knownRecipes.Contains(recipe.ID)) return ResultNotLearned;
        if (!CoversOneItem(bag, materials)) return ResultNoMaterial;
        return null;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        
        if (!span.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: CraftItem body is {Len} bytes, not the 19-byte compact frame retail sends",
                session.SessionId, span.Length);
            return;
        }
        ushort recipeId = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(RecipeOffset, 2));
        ushort quantity = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(QuantityOffset, 2));

        
        
        var batch = TryClaimBatch(session.SessionId, recipeId);
        if (batch is null)
        {
            Logger.LogInformation("{Name} asked to craft recipe {Recipe} while a batch runs; refused ({Code})",
                player.Name, recipeId, ResultBatchRunning);
            await SendRefusalAsync(session, player, ResultBatchRunning);
            return;
        }

        bool started = false;
        try
        {
            if (quantity == 0 || player.Character is not { } character)
            {
                
                
                Logger.LogInformation("{Name}: craft of recipe {Recipe} x{Qty} with no count or no character snapshot; not answered",
                    player.Name, recipeId, quantity);
                return;
            }

            var store = services.GetRequiredService<GameDataStore>();
            var recipe = store.CraftItemList.FirstOrDefault(r => r.ID == recipeId);
            var materialRow = store.CraftMaterial.FirstOrDefault(m => m.ID == recipeId);
            var learned = await services.GetRequiredService<RO2.Economy.Domain.ICraftRecipeRepository>()
                .GetAsync(player.CharacterId, ct);
            var known = CraftRecipeStore.KnownRecipeIds(store.CraftItemList,
                WorldEntryService.CreationProfessionByte(character), learned);
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var bag = await inventory.GetByCharacterAsync(player.CharacterId);

            
            if (RefusalCode(recipe, materialRow, known, bag) is { } refusal)
            {
                Logger.LogInformation("{Name} asked to craft recipe {Recipe} x{Qty}; refused ({Code})",
                    player.Name, recipeId, quantity, refusal);
                await SendRefusalAsync(session, player, refusal);
                return;
            }
            
            CraftItemList recipeRow = recipe!;
            var materials = MaterialsOf(materialRow!);

            int perTick = CraftTraitBonus.ProductPerTick(recipeRow, CraftTraitBonus.WornQuantityBonus(store.TraitInfo, bag));
            if (PlanProduct(bag, materials, recipeRow.Craft_Item_ID, perTick,
                    store.GetItem(recipeRow.Craft_Item_ID)?.Stack_Max ?? (byte)1, store.Items) is null)
            {
                
                Logger.LogInformation("{Name} has no free bag cell for the product of recipe {Recipe}; not started",
                    player.Name, recipeId);
                return;
            }

            Logger.LogInformation("{Name} starts crafting recipe {Recipe} ('{Item}') x{Qty}, {Sec}s each, {PerTick} per tick",
                player.Name, recipeId, recipeRow.Craft_ItemName, quantity, recipeRow.CraftTime_Sec, perTick);

            
            var ack = ShopItemPackets.BuildAckMakeCraftItem2539(player.EntityId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnAckMakeCraftItem, w => w.WriteBytes(ack));
            await SendToCrafterAndNearbyAsync(Broadcast, session, player, ZoneS2COpcodes.OnNotifyMakeCraftItem,
                ShopItemPackets.BuildNotifyMakeCraftItem2540(player.EntityId, ShopItemPackets.CraftPhaseBegin, recipeId));
            await SendToCrafterAndNearbyAsync(Broadcast, session, player, ZoneS2COpcodes.OnChangeState,
                CombatPackets.BuildChangeState2398(player.EntityId, CraftingState, targetHandle: 0, trailing: ChangeStateTrailer));

            started = true;
            batch.Gate.Release();
            _ = Task.Run(() => RunBatchAsync(session, player, recipeRow, materials, quantity, batch));
        }
        finally
        {
            if (!started) ReleaseUnstarted(session.SessionId, batch);
        }
    }

 /// <summary>A refused request: one 2539 with <paramref name="result"/>, sent to the crafter only.</summary>
    private static async Task SendRefusalAsync(ProudSession session, PlayerState player, short result)
    {
        var refusal = ShopItemPackets.BuildAckMakeCraftItem2539(player.EntityId, result);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckMakeCraftItem, w => w.WriteBytes(refusal));
    }

 /// <summary>The batch: one crafted item per craft time step, until the quantity is made or the batch ends.</summary>
    private async Task RunBatchAsync(ProudSession session, PlayerState player, CraftItemList recipe,
        IReadOnlyList<(uint ItemId, int Count)> materials, int quantity, Batch batch)
    {
        var token = batch.Cts.Token;
        var tick = TimeSpan.FromSeconds(Math.Max(1, (int)recipe.CraftTime_Sec));
        try
        {
            for (int made = 0; made < quantity; made++)
            {
                await Task.Delay(tick, token);
                await batch.Gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (token.IsCancellationRequested) return;
                    if (!PlayerManager.TryGetPlayer(session.SessionId, out var current) || !ReferenceEquals(current, player))
                    {
                        Batches.TryRemove(new KeyValuePair<uint, Batch>(session.SessionId, batch));
                        return;
                    }

                    using var scope = ScopeFactory.CreateScope();
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
                    var store = scope.ServiceProvider.GetRequiredService<GameDataStore>();
                    var bag = await inventory.GetByCharacterAsync(player.CharacterId);
                    if (!CoversOneItem(bag, materials))
                    {
                        
                        
                        Logger.LogInformation("{Name}: recipe {Recipe} ran out of materials after {Made} of {Qty}",
                            player.Name, batch.RecipeId, made, quantity);
                        Batches.TryRemove(new KeyValuePair<uint, Batch>(session.SessionId, batch));
                        await SendEndAsync(Broadcast, session, player, ShopItemPackets.CraftPhaseTerminate, NoRecipe);
                        return;
                    }

                    
                    
                    int perTick = CraftTraitBonus.ProductPerTick(recipe,
                        CraftTraitBonus.WornQuantityBonus(store.TraitInfo, bag));

                    
                    
                    byte productMax = store.GetItem(recipe.Craft_Item_ID)?.Stack_Max ?? (byte)1;
                    var room = PlanProduct(bag, materials, recipe.Craft_Item_ID, perTick, productMax, store.Items);
                    if (room is null)
                    {
                        
                        
                        
                        Logger.LogInformation("{Name}: no free bag cell for recipe {Recipe} after {Made} of {Qty}; stopped",
                            player.Name, batch.RecipeId, made, quantity);
                        Batches.TryRemove(new KeyValuePair<uint, Batch>(session.SessionId, batch));
                        await SendEndAsync(Broadcast, session, player, ShopItemPackets.CraftPhaseTerminate, NoRecipe);
                        return;
                    }

                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    var craftGain = EffectService.ApplyExpRate(recipe.CraftExp,
                        scope.ServiceProvider.GetRequiredService<EffectService>().ProJobExpRateOf(player));
                    await ProfessionExp.GrantAsync(session, player, craftGain, store, Broadcast,
                        scope.ServiceProvider.GetRequiredService<ICharacterRepository>(), Logger, scope.ServiceProvider);

                    
                    foreach (var (itemId, count) in materials)
                        foreach (var record in await ConsumeAsync(inventory, bag, itemId, count))
                        {
                            var destroy = ShopItemPackets.BuildDestroyItem2379(player.EntityId, record);
                            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, w => w.WriteBytes(destroy));
                        }

                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    var placed = await PlaceProductAsync(inventory, player.CharacterId, recipe.Craft_Item_ID,
                        perTick, productMax, room);
                    var sp = (ushort)Math.Clamp(player.Mp, 0, ushort.MaxValue);
                    foreach (var stack in placed)
                    {
                        var info = ShopItemPackets.BuildAckMakeCraftItemInfo2541(player.EntityId, sp,
                            WorldEntryService.BuildItemRecord126(stack));
                        await session.SendRmiAsync(ZoneS2COpcodes.OnAckMakeCraftItemInfo, w => w.WriteBytes(info));
                    }
                }
                finally
                {
                    batch.Gate.Release();
                }
            }

            
            await batch.Gate.WaitAsync(CancellationToken.None);
            try
            {
                if (token.IsCancellationRequested) return;
                Batches.TryRemove(new KeyValuePair<uint, Batch>(session.SessionId, batch));
                var cool = ShopItemPackets.BuildCoolTimeWaitCraftIndex2536(batch.RecipeId, recipe.CoolTime_Min);
                await session.SendRmiAsync(ZoneS2COpcodes.OnNotifyCoolTimeWaitCraftIndex, w => w.WriteBytes(cool));
                await SendEndAsync(Broadcast, session, player, ShopItemPackets.CraftPhaseComplete, batch.RecipeId);
                Logger.LogInformation("{Name} finished crafting recipe {Recipe} x{Qty}", player.Name, batch.RecipeId, quantity);
            }
            finally
            {
                batch.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            
            
        }
        catch (Exception ex)
        {
            Batches.TryRemove(new KeyValuePair<uint, Batch>(session.SessionId, batch));
            Logger.LogWarning(ex, "{Name}: craft batch of recipe {Recipe} failed", player.Name, batch.RecipeId);
        }
    }

 /// <summary>
 /// Sends the end of a batch: 2540 with <paramref name="phase"/> and the state update, to the crafter and nearby
 /// players. Phase 3 (stopped) carries recipe 0.
 /// </summary>
    internal static async Task SendEndAsync(BroadcastService broadcast, ProudSession session, PlayerState player,
        byte phase, ushort recipeId)
    {
        await SendToCrafterAndNearbyAsync(broadcast, session, player, ZoneS2COpcodes.OnNotifyMakeCraftItem,
            ShopItemPackets.BuildNotifyMakeCraftItem2540(player.EntityId, phase, recipeId));
        await SendToCrafterAndNearbyAsync(broadcast, session, player, ZoneS2COpcodes.OnChangeState,
            CombatPackets.BuildChangeState2398(player.EntityId, NormalState, targetHandle: 0, trailing: ChangeStateTrailer));
    }

 /// <summary>Sends to the crafter's own session, then to the nearby players on its map.</summary>
    private static async Task SendToCrafterAndNearbyAsync(BroadcastService broadcast, ProudSession session,
        PlayerState player, ushort opcode, byte[] body)
    {
        await session.SendRmiAsync(opcode, w => w.WriteBytes(body));
        await broadcast.BroadcastToNearbyAsync(player, opcode, w => w.WriteBytes(body));
    }

 /// <summary>The materials of a recipe as (item id, count per item). Also used for a private-shop craft sale.</summary>
    internal static List<(uint ItemId, int Count)> MaterialsOf(CraftMaterial row)
    {
        
        var list = new List<(uint ItemId, int Count)>();
        void Add(uint id, byte n) { if (id != 0 && n > 0) list.Add((id, n)); }
        Add(row.MaterialID0, row.count0);
        Add(row.MaterialID1, row.count1);
        Add(row.MaterialID2, row.count2);
        Add(row.MaterialID3, row.count3);
        Add(row.MaterialID4, row.count4);
        Add(row.MaterialID5, row.count5);
        return list;
    }

    private static IEnumerable<InventoryItem> BagStacks(IEnumerable<InventoryItem> bag, uint itemId) =>
        bag.Where(i => i.StorageType == StorageBag && i.ItemId == itemId && i.Count > 0).OrderBy(i => i.Slot);

    internal static bool CoversOneItem(IReadOnlyList<InventoryItem> bag, IReadOnlyList<(uint ItemId, int Count)> materials) =>
        materials.GroupBy(m => m.ItemId)
            .All(g => BagStacks(bag, g.Key).Sum(i => (long)i.Count) >= g.Sum(m => (long)m.Count));

 /// <summary>
 /// Takes <paramref name="count"/> of <paramref name="itemId"/> from the bag, lowest slot first, and returns each
 /// touched stack as it now stands. A count of 0 empties the cell.
 /// </summary>
    private static async Task<List<byte[]>> ConsumeAsync(IInventoryRepository inventory, IReadOnlyList<InventoryItem> bag,
        uint itemId, int count)
    {
        var records = new List<byte[]>();
        foreach (var stack in await TakeMaterialAsync(inventory, bag, itemId, count))
        {
            var record = WorldEntryService.BuildItemRecord126(stack);
            
            if (stack.Count <= 0) record[ItemRecordCountOffset] = 0;   // count 0 -> the client empties the cell
            records.Add(record);
        }
        return records;
    }

 /// <summary>
 /// Takes <paramref name="count"/> of <paramref name="itemId"/> from the bag, lowest slot first, and returns each
 /// touched stack with its count as it now stands. A stack that is used up has count 0 and is removed from the
 /// inventory.
 /// </summary>
 /// <remarks>
 /// Stacks are consumed in slot order: a material held in several stacks is taken from the first one until it is
 /// empty, then from the next.
 /// </remarks>
    internal static async Task<List<InventoryItem>> TakeMaterialAsync(IInventoryRepository inventory,
        IReadOnlyList<InventoryItem> bag, uint itemId, int count)
    {
        var touched = new List<InventoryItem>();
        foreach (var stack in BagStacks(bag, itemId).ToList())
        {
            if (count <= 0) break;
            int take = Math.Min(count, stack.Count);
            stack.Count -= take;
            count -= take;
            if (stack.Count <= 0)
            {
                stack.Count = 0;
                await inventory.RemoveItemAsync(stack.Id);
            }
            else
            {
                await inventory.UpdateItemAsync(stack);
            }
            touched.Add(stack);
        }
        return touched;
    }

 /// <summary>
 /// Craft_ItemNum: the number of items one tick makes, before the crafter's trait bonus
 /// (<see cref="CraftTraitBonus.ProductPerTick"/>) is added.
 /// </summary>
    internal static int ProductPerTick(CraftItemList recipe) => Math.Max(1, (int)recipe.Craft_ItemNum);

 /// <summary>
 /// Where one tick's product goes: the held stack it tops up (<paramref name="MergeInto"/>), or else the free cells
 /// its new stacks take (<paramref name="NewCells"/>).
 /// </summary>
    internal sealed record ProductPlacement(InventoryItem? MergeInto, IReadOnlyList<short> NewCells);

 /// <summary>
 /// Plans where one tick's <paramref name="quantity"/> of <paramref name="productId"/> goes, without writing
 /// anything. Returns null when the bag has too few free cells.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Stacking tops up the lowest-slot stack that the quantity fits into; otherwise it opens new stacks of the maximum
 /// size. New cells come from <see cref="BagCapacity.FreeCells"/>, which counts only the cells the player has: the
 /// main bag and each equipped bag.
 /// </para>
 /// <para>
 /// A cell that this tick's materials empty counts as free, because the material updates are sent before the
 /// product's update.
 /// </para>
 /// <para>
 /// The shipped tables fit one tick into one stack, and no recipe uses its own product as a material.
 /// </para>
 /// </remarks>
    internal static ProductPlacement? PlanProduct(IReadOnlyList<InventoryItem> bag,
        IReadOnlyList<(uint ItemId, int Count)> materials, uint productId, int quantity, byte stackMax,
        IReadOnlyDictionary<uint, ItemData> items)
    {
        // Merge candidates come from the full bag (StorageBag, Count>0; no Slot>=0 filter here); free cells exclude
        // cells this tick's materials empty. The placement rule itself is the shared Inventory.Domain primitive;
        // PlaceProductAsync keeps the per-cell write loop (min(perStack, remaining) each), so only the cells are mapped.
        var candidates = bag
            .Where(i => i.StorageType == StorageBag && i.Count > 0)
            .Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot))
            .ToList();
        var emptied = StacksEmptiedBy(bag, materials);
        var free = BagCapacity.FreeCells(bag.Where(i => !emptied.Contains(i)), items);
        var plan = BagStackPlanner.Plan(candidates, free, productId, quantity, stackMax);
        if (plan is null) return null;
        if (plan.RestackId is { } id) return new ProductPlacement(bag.First(i => i.Id == id), Array.Empty<short>());
        return new ProductPlacement(null, plan.NewStacks.Select(s => s.Cell).ToList());
    }

 /// <summary>The bag stacks that one item's worth of <paramref name="materials"/> takes to 0, in slot order, as <see cref="ConsumeAsync"/> does.</summary>
    private static HashSet<InventoryItem> StacksEmptiedBy(IReadOnlyList<InventoryItem> bag,
        IReadOnlyList<(uint ItemId, int Count)> materials)
    {
        var emptied = new HashSet<InventoryItem>();
        foreach (var g in materials.GroupBy(m => m.ItemId))
        {
            long need = g.Sum(m => (long)m.Count);
            foreach (var stack in BagStacks(bag, g.Key))
            {
                if (need <= 0) break;
                if (stack.Count <= need) emptied.Add(stack);
                need -= stack.Count;
            }
        }
        return emptied;
    }

 /// <summary>
 /// Writes a <see cref="PlanProduct"/> result and returns each stack the product landed in, as it now stands. Also
 /// places the product of a private-shop craft sale.
 /// </summary>
    internal static async Task<List<InventoryItem>> PlaceProductAsync(IInventoryRepository inventory, uint characterId,
        uint productId, int quantity, byte stackMax, ProductPlacement plan)
    {
        if (plan.MergeInto is { } target)
        {
            target.Count += quantity;
            await inventory.UpdateItemAsync(target);
            return new List<InventoryItem> { target };
        }

        int perStack = stackMax < 1 ? 1 : stackMax;
        var placed = new List<InventoryItem>();
        int remaining = quantity;
        foreach (var cell in plan.NewCells)
        {
            var stack = new InventoryItem
            {
                CharacterId = characterId,
                ItemId = productId,
                Count = Math.Min(perStack, remaining),
                Slot = cell,
                StorageType = StorageBag,
            };
            await inventory.AddItemAsync(stack);   
            placed.Add(stack);
            remaining -= stack.Count;
        }
        return placed;
    }
}
