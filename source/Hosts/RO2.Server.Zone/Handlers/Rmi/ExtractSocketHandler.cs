using System.Buffers.Binary;
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
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1544 ReqExtractSocket - pull the rune out of one runehole with rune pincers.</summary>
/// <remarks>
/// <para>
/// REQUEST. 17 bytes, no envelope: <c>[u64 toolUid][u64 targetUid][u8 hole]</c>.
/// </para>
/// <para>
/// Behavior: the tool at @0 is Rune Pincers (60000203, ItemInfo Item_Type 50);
/// 2329 OnUseItem on the tool with result 0 and useSource 1 precedes every 2787.
/// The 2787 is <c>[u16 result][126-byte record of the target][126-byte record of
/// the extracted item]</c> = 254 bytes: the target's record has hole @16 with the value cleared and
/// the kind kept - and, where the previous record is known, the puncher is kept and
/// the other holes are unchanged - and the second record is the extracted rune in the bag - the stack it
/// landed in, with counts up to 20, so it stacks like any other gain. The template that comes out is the
/// ItemReinforceInfo row the hole's value names (see <see cref="ItemWorkbench.ReinforceId"/>). When the last
/// pincers go (2329 remaining 0) and the rune opens a new stack, it often lands in the pincers' own cell,
/// so the cell search below counts that cell as free.
/// </para>
/// <para>
/// REFUSAL. A non-zero 2787 is the code over 252 zero bytes. The code is the
/// client's own table (string_ui): 1 = 11093 "Rune Hole Puncher
/// is not found.", 2 = 10418 "Unable to find Target item", 3 = 10419, 4 = 10420 "Wrong socket slot index", 5 = 10421,
/// 6 = 11094, 7 = 11095 "Failed to retrieve the item storage.", 9 = 11096, 10 = 11235; any non-zero code also resets
/// the craft input. The refusals map onto <see cref="Check"/>: code 1
/// for a rune sent as the tool and for a pincer stack its own 2329 had
/// just emptied, code 4 for a hole that is opened (kind 6) but holds value 0, i.e. empty. Nothing is consumed
/// on a refusal.
/// </para>
/// </remarks>
public class ExtractSocketHandler : ZoneRmiHandlerBase<ExtractSocketHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqExtractSocket;
    public override string Name => "ExtractSocket";

 /// <summary>1544 body length: 17 bytes.</summary>
    private const int RequestBodyLength = 17;

 /// <summary>1544 body offset of the u64 uid of the tool (the rune pincers stack).</summary>
    private const int ToolUidOffset = 0;

 /// <summary>1544 body offset of the u64 uid of the target item whose rune hole is emptied.</summary>
    private const int TargetUidOffset = 8;

 /// <summary>1544 body offset of the u8 zero-based rune hole index.</summary>
    private const int HoleIndexOffset = 16;

 /// <summary>InventoryItem.StorageType of an item held in the bag.</summary>
    private const byte StorageBag = 0;

 /// <summary>InventoryItem.StorageType of an item that is worn.</summary>
    private const byte StorageEquipped = 1;

 /// <summary>2329 OnUseItem useSource of the consumed tool: 1, the value an item use answers with.</summary>
    private const byte UseSourceTool = CombatPackets.UseSourceItemUse;

 /// <summary>Size of the u16 result at the start of the 2787 body; two item records follow it.</summary>
    private const int ResultLength = 2;

 /// <summary>Slot value of an item that has not been given a bag cell yet.</summary>
    private const short NoCell = -1;

 /// <summary>ItemInfo Item_Type of Rune Pincers (the 13 '룬 펜치' / Rune Remover templates).</summary>
    private const byte RunePincersItemType = 50;

 /// <summary>2787 result 0: the rune came out.</summary>
    internal const ushort Extracted = 0;

 /// <summary>Code 1 = string_ui 11093 "Rune Hole Puncher is not found.": the tool is gone, not the player's, not
 /// in the bag, or not rune pincers. Examples are a rune used as the tool and a stack
 /// its 2329 had just emptied.</summary>
    internal const ushort PincersNotFound = 1;

 /// <summary>Code 2 = string_ui 10418 "Unable to find Target item": the target is gone, not the player's, or
 /// neither in the bag nor worn.</summary>
    internal const ushort TargetNotFound = 2;

 /// <summary>Code 4 = string_ui 10420 "Wrong socket slot index": the hole is not opened, or it is opened but holds
 /// nothing, e.g. an opened, empty hole 0.</summary>
    internal const ushort WrongSocketIndex = 4;

 /// <summary>
 /// Code 7 = string_ui 11095 "Failed to retrieve the item storage.": the rune would open a new stack and the
 /// bag has no usable cell for it. 7 is the client's
 /// code whose text names the bag. Without the check the rune would be created in a cell the client cannot
 /// show (InventoryRepository.AddItemAsync with Slot -1), after the hole had been emptied and the pincers spent.
 /// </summary>
    internal const ushort NoBagCell = 7;

    public ExtractSocketHandler(IServiceScopeFactory s, ILogger<ExtractSocketHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: ExtractSocket body is {Len} bytes, not the expected 17",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        ulong toolUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ToolUidOffset));
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(TargetUidOffset));
        int hole = span[HoleIndexOffset];

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var tool = toolUid != targetUid ? await inventory.GetByIdAsync(toolUid) : null;
        var target = await inventory.GetByIdAsync(targetUid);

        ushort code = Check(store, player.CharacterId, tool, target, hole, out var why);
        uint extractedId = 0;
        InventoryItem? joinStack = null;
        short cell = NoCell;
        if (code == Extracted)
        {
            // Where the rune will go, decided BEFORE anything is spent (see NoBagCell).
            extractedId = ItemSockets.ParseSockets(target!.CardsJson)[hole];
            var owned = await inventory.GetByCharacterAsync(player.CharacterId);
            (joinStack, cell) = PlaceExtracted(owned, tool!, extractedId, store);
            if (joinStack is null && cell < 0)
            {
                code = NoBagCell;
                why = "the bag has no usable cell for the rune";
            }
        }

        if (code != Extracted)
        {
            Logger.LogInformation("{Name} extract-socket #{Hole} from {Target} with {Tool} refused ({Code}): {Why}",
                player.Name, hole + 1, targetUid, toolUid, code, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsExtractSocket, (ReadOnlyMemory<byte>)BuildRefusal2787(code));
            return;
        }

        // Empty the hole; it stays open (the array keeps its length), as the client's record keeps kind and puncher.
        var holes = ItemSockets.ParseSockets(target!.CardsJson);
        holes[hole] = 0;
        target.CardsJson = ItemSockets.SerializeSockets(holes);
        await inventory.UpdateItemAsync(target);

        // Consume one tool: 2329 OnUseItem, result 0, remaining = the stack after.
        int left = tool!.Count - 1;
        var (tBag, tSlot) = ItemWorkbench.ClientBagSlot(tool);
        if (left <= 0) { await inventory.RemoveItemAsync(tool.Id); left = 0; }
        else { tool.Count = left; await inventory.UpdateItemAsync(tool); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, tSlot, tBag, tool.Id, (byte)Math.Clamp(left, 0, byte.MaxValue), useSource: UseSourceTool)));

        // The extracted item goes to the bag the way any gain stacks (InventoryStacking's rule: one more on the
        // first stack of the template that still fits under Stack_Max), else into the cell chosen above.
        InventoryItem placed;
        if (joinStack is not null)
        {
            joinStack.Count += 1;
            await inventory.UpdateItemAsync(joinStack);
            placed = joinStack;
        }
        else
        {
            placed = new InventoryItem
            {
                CharacterId = player.CharacterId,
                ItemId = extractedId,
                Count = 1,
                Slot = cell,                  // a real free cell (BagCapacity), never Slot -1
                StorageType = StorageBag,     // Durability stays -1, which the record writer resolves to the template's
            };
            await inventory.AddItemAsync(placed);
        }
        var extractedRecord = WorldEntryService.BuildItemRecord126(placed);

        // AnsExtractSocket (2787): fixed 254-byte body =
        // [u16 result][126-byte target record][126-byte record of the extracted item's stack].
        var targetRecord = ItemWorkbench.RecordWithRuneholes(target, store);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsExtractSocket, writer =>
        {
            writer.WriteUInt16(Extracted);       // body+0 result
            writer.WriteBytes(targetRecord);     // body+2
            writer.WriteBytes(extractedRecord);  // body+128
        });

        Logger.LogInformation("{Name} extracted {Rune} from hole #{Hole} of '{Item}' ({Uid}), {Left} pincers left",
            player.Name, extractedId, hole + 1, store.GetItem(target.ItemId)?.Name ?? target.ItemId.ToString(), target.Id, left);

        // A worn item just lost a rune: re-push the 2498 sheet and the cached combat stats, the same
        // recompute AttachSocketHandler (1543) does when a rune goes in. The sheet keeps the active effects
        // (EffectService.RefreshPcTraitSheet), and sets the runtime max HP/SP and the combat cache from it.
        if (target.StorageType == StorageEquipped && player.Character is { } ch)
        {
            var equipped = await inventory.GetEquipmentAsync(ch.Id);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, equipped));
            (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, equipped);
        }

        // The card taken out is a bag arrival: 2588 when it is new to the book, and for an MVP card always.
        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { extractedId },
            services, Logger);
    }

 /// <summary>
 /// The 2787 refusal code for a 1544, or <see cref="Extracted"/> when the extraction may go ahead. The checks run
 /// in the order of the client's code table: the tool (1), then the target (2), then the hole (4). The bag-space
 /// check (<see cref="NoBagCell"/>) needs the whole bag and is <see cref="PlaceExtracted"/>'s.
 /// </summary>
    internal static ushort Check(GameDataStore store, uint characterId, InventoryItem? tool, InventoryItem? target,
        int hole, out string why)
    {
        why = "";
        if (tool is null || tool.CharacterId != characterId || tool.StorageType != StorageBag || tool.Count < 1)
        {
            why = "tool is not a stack in the bag";
            return PincersNotFound;
        }
        if (store.GetItem(tool.ItemId)?.Item_Type != RunePincersItemType)
        {
            why = $"item {tool.ItemId} is not rune pincers (Item_Type {RunePincersItemType})";
            return PincersNotFound;
        }
        if (target is null || target.CharacterId != characterId || target.StorageType is not (StorageBag or StorageEquipped))
        {
            why = "target is not in the bag or worn";
            return TargetNotFound;
        }
        var holes = ItemSockets.ParseSockets(target.CardsJson);
        if (hole >= ItemWorkbench.OpenedHoles(target))
        {
            why = $"hole #{hole + 1} is not open";
            return WrongSocketIndex;
        }
        if (holes[hole] == 0)
        {
            why = $"hole #{hole + 1} is empty";
            return WrongSocketIndex;
        }
        return Extracted;
    }

 /// <summary>
 /// Where one extracted <paramref name="runeId"/> goes: the first bag stack of it (cell order) that still takes one
 /// more under ItemInfo Stack_Max - InventoryStacking's rule, which extractions follow (count +1 on the
 /// stack's own uid) - or else the first usable free cell (<see cref="BagCapacity.FreeCells"/>),
 /// counting the pincers' cell as free when this extraction spends their last one. (null, -1) = nowhere.
 /// </summary>
    internal static (InventoryItem? Join, short Cell) PlaceExtracted(IReadOnlyList<InventoryItem> owned,
        InventoryItem tool, uint runeId, GameDataStore store)
    {
        byte stackMax = store.GetItem(runeId)?.Stack_Max ?? 1;
        var candidates = owned
            .Where(i => i.StorageType == StorageBag && i.Slot >= 0)
            .Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot))
            .ToList();
        var afterSpend = tool.Count <= 1 ? owned.Where(i => i.Id != tool.Id) : owned;
        var free = BagCapacity.FreeCells(afterSpend, store.Items);
        // One extracted rune (quantity 1): the shared Inventory.Domain rule folds it into the lowest-cell stack
        // that still fits, else opens one new stack in the first free cell, else (null, NoCell).
        var plan = BagStackPlanner.Plan(candidates, free, runeId, 1, stackMax);
        if (plan is null) return (null, NoCell);
        if (plan.RestackId is { } id) return (owned.First(i => i.Id == id), NoCell);
        return (null, plan.NewStacks[0].Cell);
    }

 /// <summary>
 /// A refused 2787: <c>[u16 code][252 zero bytes]</c>, 254 bytes like every 2787; a non-zero
 /// result carries both records zeroed.
 /// </summary>
    internal static byte[] BuildRefusal2787(ushort code)
    {
        var body = new byte[ResultLength + 2 * ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        return body;
    }
}
