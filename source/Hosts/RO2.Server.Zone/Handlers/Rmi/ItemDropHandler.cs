using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>The fields of a 1321 ReqPCItemDrop blob (see <see cref="ItemDropHandler"/>).</summary>
public readonly record struct ItemDropRequest(ushort MsgTag, uint ActorHandle, byte Slot, ulong ItemUid, byte Extra,
    float X, float Y, float Z);

/// <summary>
/// RMI 1321 ReqPCItemDrop: the drop confirmation of an item. Parsed, checked for ownership and logged; nothing is
/// removed and nothing is answered.
/// </summary>
/// <remarks>
/// <para>
/// Wire layout, VarBlobC <c>[CompactLen 30][blob]</c>:
/// <c>+0 u16 innerSize 30, +2 u16 msgTag 58016 (0xE2A0), +4 u32 own actor handle, +8 u8 slot,
/// +9 u64 item uid, +17 u8, +18/+22/+26 f32 drop position</c>.
/// The client also holds the item count but never puts it in the blob. The slot/uid fields match the 1337
/// ReqDestroyItem descriptor that ItemDestroyHandler decodes.
/// </para>
/// <para>
/// WHY NOTHING ELSE HAPPENS. The client reads no answer to 2331 OnDropItem / 2332 OnDropItemNotify. This build has no
/// ground items (GroundItemService: loot lives on the corpse), and the effect of a drop on the item is not
/// defined, so removing it here would destroy it under an invented rule. Fields are read at their blob offsets
/// (not shifted by the compact prefix), and no 2331 or 2332 is sent.
/// </para>
/// </remarks>
public class ItemDropHandler : ZoneRmiHandlerBase<ItemDropHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemDrop;
    public override string Name => "ItemDrop";
    public ItemDropHandler(IServiceScopeFactory s, ILogger<ItemDropHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Blob length of a 1321 (30 bytes); also the value of the blob's own innerSize field and of the compact length.</summary>
    public const int BlobLength = 30;

    /// <summary>Blob offset of the u16 msgTag.</summary>
    private const int MsgTagOffset = 2;

    /// <summary>Blob offset of the u32 own actor handle.</summary>
    private const int ActorHandleOffset = 4;

    /// <summary>Blob offset of the u8 inventory slot byte.</summary>
    private const int SlotOffset = 8;

    /// <summary>Blob offset of the u64 item uid.</summary>
    private const int ItemUidOffset = 9;

    /// <summary>Blob offset of the u8 extra byte (meaning not defined).</summary>
    private const int ExtraOffset = 17;

    /// <summary>Blob offset of the f32 drop position X; Y and Z follow at 4-byte steps.</summary>
    private const int PositionXOffset = 18;

    /// <summary>Blob offset of the f32 drop position Y.</summary>
    private const int PositionYOffset = PositionXOffset + sizeof(float);

    /// <summary>Blob offset of the f32 drop position Z.</summary>
    private const int PositionZOffset = PositionYOffset + sizeof(float);

    /// <summary>Decodes a 1321 body (compact prefix included). False when the envelope is malformed or the blob is
    /// shorter than <see cref="BlobLength"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ItemDropRequest request)
    {
        request = default;
        // Lenient: the declared length is not compared, only that the blob reaches its last field.
        if (!payload.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < BlobLength) return false;
        request = new ItemDropRequest(
            MsgTag: BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(MsgTagOffset)),
            ActorHandle: BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(ActorHandleOffset)),
            Slot: blob[SlotOffset],
            ItemUid: BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(ItemUidOffset)),
            Extra: blob[ExtraOffset],
            X: BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(PositionXOffset)),
            Y: BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(PositionYOffset)),
            Z: BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(PositionZOffset)));
        return true;
    }

    /// <summary>msgTag at blob+2 (58016 = 0xE2A0).</summary>
    public const ushort MsgTag = 58016;

    /// <summary>Container argument passed to the shared validator: the blob has no container field, so any value passes.</summary>
    private const byte NoContainerCheck = 0;

    /// <summary>Slot argument passed to the shared validator: the slot byte has no defined bound, so 0 always passes.</summary>
    private const int NoSlotCheck = 0;

    /// <summary>Quantity and stack-count arguments passed to the shared validator: the blob has no quantity, so 1 within a stack of 1 always passes.</summary>
    private const int NoQuantity = 1;

    /// <summary>The tag and actor-handle check (INV-H12, <see cref="InventoryRequest.Validate"/>). The slot byte has
    /// no defined bound and no container or quantity field exists, so only the first two apply.</summary>
    internal static InvReject CheckRequest(in ItemDropRequest req, uint playerEntityId)
        => InventoryRequest.Validate(req.MsgTag, MsgTag, req.ActorHandle, playerEntityId,
            container: NoContainerCheck, slot: NoSlotCheck, qty: NoQuantity, stackCount: NoQuantity);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var req))
        {
            Logger.LogWarning("{Name}: malformed 1321 ({Len} bytes)", player.Name, payload.Length);
            return;
        }

        var reject = CheckRequest(req, player.EntityId);
        if (reject != InvReject.None)
        {
            Logger.LogWarning("{Name}: 1321 rejected ({Reason})", player.Name, reject);
            return;
        }

        // Ownership check, as ItemDestroyHandler does: the uid space is global.
        var item = await services.GetRequiredService<IInventoryRepository>().GetByIdAsync(req.ItemUid);
        if (item is null || item.CharacterId != player.CharacterId)
        {
            Logger.LogWarning("{Name}: 1321 names uid {Uid}, which is not this character's", player.Name, req.ItemUid);
            return;
        }

        Logger.LogInformation(
            "{Name} drops uid {Uid} (item {Item}, slot byte {Slot}, extra {Extra}) at ({X},{Y},{Z}); not applied: no ground items in this build",
            player.Name, req.ItemUid, item.ItemId, req.Slot, req.Extra, req.X, req.Y, req.Z);
    }
}
