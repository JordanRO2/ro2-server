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

/// <summary>
/// Handles RMI 1514 (ReqRandomItemBatting): opens a random box on the roulette window. One box is
/// consumed and one entry of its random set is added to the bag.
/// </summary>
/// <remarks>
/// The request carries the box Item_Type and RandomSet_ID, and the box is the bag stack that matches
/// both. The prize is drawn by weight from the drawable rows of the set. The response locks the bag,
/// removes the box, sends the roulette item list and the prize, then unlocks it. Refusals take nothing
/// and answer with a result code: the player is gliding, the bag is full, or the box cannot be resolved
/// or its set has nothing to draw.
/// </remarks>
public class RandomItemBattingHandler : ZoneRmiHandlerBase<RandomItemBattingHandler>
{
    private const int RequestLength = 5;

 /// <summary>Returns whether an Item_Type opens a random box through RMI 1514.</summary>
    internal static bool IsRandomBoxType(byte itemType) => itemType is 23 or 36 or 59;

    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomItemBatting;
    public override string Name => "RandomItemBatting";
    public RandomItemBattingHandler(IServiceScopeFactory s, ILogger<RandomItemBattingHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != RequestLength)
        {
            Logger.LogWarning("{Name}: 1514 body is {Len} bytes, expected {Expected}; window closed",
                player.Name, payload.Length, RequestLength);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultCannotOpen, ct);
            return;
        }
        byte itemType = payload.Span[0];
        uint setId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(1));

        if (player.PendingGliderDismount)
        {
            Logger.LogInformation("{Name}: 1514 random box (type {Type}, set {Set}) refused while gliding",
                player.Name, itemType, setId);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultFlying, ct);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);

        var box = FindBox(owned, store.Items, itemType, setId);
        if (box is null)
        {
            Logger.LogInformation("{Name}: 1514 random box (type {Type}, set {Set}) refused: no bag stack of a box template with that pair",
                player.Name, itemType, setId);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultCannotOpen, ct);
            return;
        }
        var candidates = ItemChangeExchange.Candidates(store.RandomSet, setId);
        long totalRatio = candidates.Sum(r => (long)r.Ratio);
        var drawn = totalRatio > 0 ? ItemChangeExchange.Pick(candidates, Random.Shared.NextInt64(totalRatio)) : null;
        if (drawn is null)
        {
            Logger.LogInformation("{Name}: 1514 random box {Box} refused: RandomSet {Set} has no drawable row",
                player.Name, box.ItemId, setId);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultCannotOpen, ct);
            return;
        }
        uint prizeId = drawn.Item;
        int prizeCount = Math.Max(1, (int)drawn.Package);

        
        
        IReadOnlyList<InventoryItem> afterBox = box.Count <= 1 ? owned.Where(i => i.Id != box.Id).ToList() : owned;
        var landing = ItemPickUpHandler.PlanLanding(afterBox, player.CharacterId, prizeId, prizeCount, store.Items);
        if (landing.Refused || landing.MergeInto?.Id == box.Id)
        {
            Logger.LogInformation("{Name}: 1514 box {Box}: no bag room for {Count}x {Prize}; box kept",
                player.Name, box.ItemId, prizeCount, prizeId);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultBagFull, ct);
            return;
        }

        
        
        var boxRecord = WorldEntryService.BuildItemRecord126(box);
        byte bag = boxRecord[15], slot = boxRecord[16];
        int remaining = box.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(box.Id); remaining = 0; }
        else { box.Count = remaining; await inventory.UpdateItemAsync(box); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, box.Id, (byte)Math.Clamp(remaining, 0, 255)), ct);

        
        var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, prizeId, Logger);
        if (landed.Count == 0)
        {
            Logger.LogWarning("{Name}: 1514 box {Box} was consumed but its prize {Count}x {Prize} could not be saved",
                player.Name, box.ItemId, prizeCount, prizeId);
            await AnswerAsync(session, 0, RandomBoxPackets.ResultCannotOpen, ct);
            return;
        }
        foreach (var stack in landed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnCreateItem,
                ItemTransformHandler.BuildCreateItem2335(player.EntityId, WorldEntryService.BuildItemRecord126(stack)), ct);

        
        await KharaService.OnHeldItemChangedAsync(session, player, prizeId, services, Logger, ct);
        await services.GetRequiredService<LootService>().AdvanceCollectQuestsAsync(session, player, prizeId, prizeCount);
        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { prizeId }, services, Logger);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsRandomItemInBoxList,
            RandomBoxPackets.Build2713(InBoxListItems(candidates)), ct);
        await AnswerAsync(session, prizeId, RandomBoxPackets.ResultOk, ct);
        Logger.LogInformation("{Name} opened random box {Box} (set {Set}): {Count}x {Prize} ({Left} boxes left)",
            player.Name, box.ItemId, setId, prizeCount, prizeId, remaining);
    }

 /// <summary>
 /// Finds the bag stack named by a 1514 request: a held random box with the given Item_Type and
 /// RandomSet_ID. Returns the lowest matching cell, or null when none qualifies.
 /// </summary>
    internal static InventoryItem? FindBox(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items,
        byte itemType, uint setId)
    {
        if (!IsRandomBoxType(itemType) || setId == 0) return null;
        return owned
            .Where(i => i.StorageType == 0 && i.Slot >= 0 && i.Count > 0
                        && items.TryGetValue(i.ItemId, out var t) && t.Item_Type == itemType && t.RandomSet_ID == setId)
            .OrderBy(i => i.Slot)
            .FirstOrDefault();
    }

 /// <summary>
 /// Builds the roulette item list: the distinct item of each drawable row, in table order.
 /// </summary>
    internal static List<uint> InBoxListItems(IReadOnlyList<RandomSet> candidates)
    {
        var ids = new List<uint>(candidates.Count);
        var seen = new HashSet<uint>();
        foreach (var r in candidates)
            if (r.Item != 0 && seen.Add(r.Item)) ids.Add(r.Item);
        return ids;
    }

    private static Task AnswerAsync(ProudSession session, uint wonItemId, ushort result, CancellationToken ct)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsRandomItemSelected, RandomBoxPackets.Build2714(wonItemId, result), ct);
}
