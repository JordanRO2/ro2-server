using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1485 ReqGuildStorageItemPut: deposits one bag item into the guild vault.</summary>
/// <remarks>
/// <para>
/// The request is 18 bytes: [16-byte guild GUID][u8 bag][u8 slot], identifying the item by its bag cell. The item is moved, not copied: it is removed from the character's inventory and stored in the guild vault with its UID, in the lowest free cell below the guild's capacity (<see cref="GuildVaultCapacity"/>).
/// </para>
/// <para>
/// A successful deposit is answered with 2379 OnDestroyItem (empties the bag cell), 2670 (result 0), and 2675 NfyAddGuildStorage_Item (adds the item to the vault window). A full vault is refused with result 29, and a put with nothing to move with result 28. The 2675 goes to the depositor and to every other member whose vault window is open (<see cref="GuildVaultWindows"/>).
/// </para>
/// </remarks>
public class GuildStorageItemPutHandler : ZoneRmiHandlerBase<GuildStorageItemPutHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildStorageItemPut;
    public override string Name => "GuildStorageItemPut";
    public GuildStorageItemPutHandler(IServiceScopeFactory s, ILogger<GuildStorageItemPutHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Size of the guild GUID that opens the request body.</summary>
    private const int GuildGuidLength = 16;

 /// <summary>Request body length: the guild GUID and the bag cell.</summary>
    internal const int RequestLength = SlotOffset + 1;

 /// <summary>Offset of the bag cell in the request body, after the 16-byte guild GUID.</summary>
    internal const int BagOffset = GuildGuidLength;

 /// <summary>Offset of the slot within the bag, right after <see cref="BagOffset"/>.</summary>
    internal const int SlotOffset = BagOffset + 1;

 /// <summary>Result 0 of 2670: the item was deposited.</summary>
    internal const ushort ResultOk = 0;

 /// <summary>Result 28: nothing can be moved, for example because the bag cell is empty or the item is no longer in it.</summary>
    internal const ushort ResultNothingToPut = 28;

 /// <summary>
 /// Result 29: no vault cell below the guild's capacity is free. The client shows its guild storage full message for this code.
 /// </summary>
    internal const ushort ResultVaultFull = 29;

 /// <summary>Message tag of the 2379 OnDestroyItem notice sent with a deposit.</summary>
    internal const ushort MsgTagDestroyItem = ShopItemPackets.MsgTagDestroyItem;

 /// <summary>Reason value in the 2379 OnDestroyItem notice for a guild deposit. Other reasons use different values.</summary>
    internal const ushort DestroyReasonGuildPut = 1;

 /// <summary>Pickup message selector for a deposit refused because the vault is full (2333 body offset 2).</summary>
    internal const ushort PickupKindGuildReturn = 1;

 /// <summary>Result code of the 2333 OnPickupItem sent with a refused deposit: 0, the item simply returns to its cell.</summary>
    private const ushort PickupResultOk = 0;

 /// <summary><see cref="InventoryItem.StorageType"/> of a plain bag cell (flat slot space, main bag plus extra bags).</summary>
    private const byte StorageBag = 0;

 // Client bag addressing: container 1 is the main bag (slots 50-69), containers 2-4 are the extra bags (slots 0-34).
    private const byte MainBagContainer = 1;
    private const byte MainBagFirstSlot = 50;
    private const byte MainBagEndSlot = MainBagFirstSlot + BagCapacity.MainBagCells;
    private const byte FirstExtraBagContainer = 2;
    private const byte LastExtraBagContainer = MainBagContainer + BagCapacity.ExtraBagCount;

 // 2379 body layout: [msgTag][u32 PC handle @blob+4][u16 reserved @blob+8][u16 reason @blob+10][126-byte item record @blob+12].
    private const ushort DestroyReservedZero = 0;

 /// <summary>Initial buffer capacity for the 2379 body, which is 141 bytes.</summary>
    private const int DestroyBodyCapacity = 160;

 // Leading fields of the 2379 item record: [u64 uid][u32 template][u16 durability][u8 count][u8 bag][u8 slot]. The rest stays zero.
    private const int DestroyRecordHeadLength = sizeof(ulong) + sizeof(uint) + sizeof(ushort) + 3 * sizeof(byte);
    private const ushort RecordDurabilityNone = 0;

 /// <summary>Record count 0 is what makes the client empty the cell.</summary>
    private const byte RecordCountEmpty = 0;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());

        var span = payload.Span;
        byte bag = span[BagOffset];
        byte slot = span[SlotOffset];
        int flat = BagSlotToFlat(bag, slot);

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        ushort result = ResultOk;
        GuildStorageItem? stored = null;
        InventoryItem? item = null;
        int capacity = 0;

        if (guild == null || flat < 0) result = ResultNothingToPut;
        else
        {
            var inv = services.GetRequiredService<IInventoryRepository>();
            var bagItems = await inv.GetByCharacterAsync(player.CharacterId);
            
            
            
            item = bagItems.FirstOrDefault(i => i.StorageType == StorageBag && i.Slot == flat);
            if (item == null) result = ResultNothingToPut;
            else
            {
                var data = services.GetRequiredService<GameDataStore>();
                var table = GuildSkillLearnPackets.Index(data.GuildSkillLevel);
                lock (guild.Gate) capacity = GuildVaultCapacity.SlotCapacity(guild.Skills, table, data.ConfigDef);
                var (moved, refusal) = await GuildService.Instance.DepositAsync(guild, item, inv, capacity);
                stored = moved;
                result = ResultFor(refusal);
            }
        }

        Logger.LogInformation("Session {Id}: {Name} deposits bag {Bag}/{Slot} (flat {Flat}) -> result {Result} vaultSlot {V} of {Cap}",
            session.SessionId, player.Name, bag, slot, flat, result, stored?.StorageSlot ?? 0, capacity);

        if (item != null && result is ResultOk or ResultVaultFull)
        {
            
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
                (ReadOnlyMemory<byte>)BuildDestroyItem2379(player.EntityId, item.Id, item.ItemId, bag, slot));
        }
        if (item != null && result == ResultVaultFull)
        {
            
            
            
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem, (ReadOnlyMemory<byte>)LootNotifyPackets.BuildPickupItem2333(
                WorldEntryService.BuildItemRecord126(item), resultCode: PickupResultOk, pickupKind: PickupKindGuildReturn));
        }

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildStorageItemPut, writer => writer.WriteUInt16(result));

        if (result == ResultOk && guild != null && stored != null)
        {
            
            var body = GuildPackets.BuildNfyAddGuildStorageItem(stored);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyAddGuildStorage_Item, (ReadOnlyMemory<byte>)body);
            await GuildVaultWindows.SendToOtherViewersAsync(guild, session.SessionId, PlayerManager, Broadcast,
                ZoneS2COpcodes.NfyAddGuildStorage_Item, body);
            
            await services.GetRequiredService<IGuildStorageLog>().AddAsync(GuildStorageLog.Guid16Of(guild), player.Name, GuildStorageLog.KindDeposit, stored.ItemId, stored.Count);
        }
    }

 /// <summary>Returns the 2670 result code for a <see cref="GuildService.DepositAsync"/> outcome.</summary>
    internal static ushort ResultFor(GuildService.DepositRefusal refusal) => refusal switch
    {
        GuildService.DepositRefusal.None => ResultOk,
        GuildService.DepositRefusal.VaultFull => ResultVaultFull,
        _ => ResultNothingToPut,
    };

 /// <summary>
 /// Builds the 2379 OnDestroyItem body for a guild deposit. The record names only the bag cell being emptied and clears its count, which makes the client empty the cell.
 /// </summary>
    internal static byte[] BuildDestroyItem2379(uint pcHandle, ulong uid, uint templateId, byte bag, byte slot)
    {
        var rec = new byte[ItemPackets.ItemRecordLength];
        using (var rw = new PacketWriter(DestroyRecordHeadLength))
        {
            rw.WriteUInt64(uid);                        // record+0  item UID
            rw.WriteUInt32(templateId);                 // record+8  item template
            rw.WriteUInt16(RecordDurabilityNone);       // record+12 durability
            rw.WriteByte(RecordCountEmpty);             // record+14 count
            rw.WriteByte(bag);                          // record+15 bag of the emptied cell
            rw.WriteByte(slot);                         // record+16 slot of the emptied cell
            rw.GetWrittenSpan().CopyTo(rec);
        }
        using var pw = new PacketWriter(DestroyBodyCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDestroyItem);
            p.WriteUInt32(pcHandle);                    // blob+4  PC handle of the depositor
            p.WriteUInt16(DestroyReservedZero);         // blob+8  reserved
            p.WriteUInt16(DestroyReasonGuildPut);       // blob+10 reason
            p.WriteBytes(rec);                          // blob+12 item record
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Converts the client's (bag, slot) UI coordinate to a flat inventory slot. The main bag (container 1, slots 50 to 69) maps to 0 to 19, and extra bags (containers 2 to 4, slots 0 to 34) map to 20 + (bag - 2) * 35 + slot. Returns -1 for anything else, such as equipment panel slots.
 /// </summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == MainBagContainer) return slot is >= MainBagFirstSlot and < MainBagEndSlot ? slot - MainBagFirstSlot : -1;
        if (bag is >= FirstExtraBagContainer and <= LastExtraBagContainer && slot < BagCapacity.ExtraBagStride)
            return BagCapacity.MainBagCells + (bag - FirstExtraBagContainer) * BagCapacity.ExtraBagStride + slot;
        return -1;
    }
}
