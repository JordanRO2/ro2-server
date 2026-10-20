using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1580 ReqPreViewCacheItem: previews an extension ticket applied to a timed item.
/// This server keeps no timed items, so every request is refused with a 2846 AnsPreViewCacheItem result.
/// </summary>
/// <remarks>
/// Request: 20 bytes, two {bag, slot, u64 uid} records (the extension ticket, then the target item).
/// Answer: 128 bytes, a u16 result followed by a 126-byte item record. Only result 0 opens the extension
/// window, which this server never does.
/// </remarks>
public class PreViewCacheItemHandler : ZoneRmiHandlerBase<PreViewCacheItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPreViewCacheItem;
    public override string Name => "PreViewCacheItem";

 /// <summary>1580 body length.</summary>
    public const int RequestBodyLength = 20;

 /// <summary>2846 body length: u16 result followed by one 126-byte item record.</summary>
    public const int AnswerBodyLength = 128;

 /// <summary>2846 result for a target item that does not exist (client message 2323).</summary>
    public const ushort ResultItemMissing = 2;

 /// <summary>2846 result for an item that cannot be extended (client message 10658).</summary>
    public const ushort ResultCannotExtend = 3;

 /// <summary>InventoryItem.StorageType of the bag and of equipped gear.</summary>
    private const byte StorageBag = 0, StorageEquipped = 1;

    public PreViewCacheItemHandler(IServiceScopeFactory s, ILogger<PreViewCacheItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>The two slot records of a 1580 (see the class remarks).</summary>
    internal readonly record struct PreviewRequest(byte BagA, byte SlotA, ulong UidA, byte BagB, byte SlotB, ulong UidB);

 /// <summary>Parses a 1580 body; false unless it is exactly the reader's 20 bytes.</summary>
    internal static bool TryParseRequest1580(ReadOnlySpan<byte> body, out PreviewRequest request)
    {
        request = default;
        if (body.Length != RequestBodyLength) return false;
        request = new PreviewRequest(body[0], body[1], BinaryPrimitives.ReadUInt64LittleEndian(body[2..]),
            body[10], body[11], BinaryPrimitives.ReadUInt64LittleEndian(body[12..]));
        return true;
    }

 /// <summary>A 2846 refusal: a u16 result followed by an empty 126-byte record.</summary>
    internal static byte[] BuildRefusal2846(ushort result)
    {
        ArgumentOutOfRangeException.ThrowIfZero(result);
        var body = new byte[AnswerBodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest1580(payload.Span, out var req))
        {
            Logger.LogWarning("Session {Id}: PreViewCacheItem body is {Len} bytes, not the reader's {Want}",
                session.SessionId, payload.Length, RequestBodyLength);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var target = await inventory.GetByIdAsync(req.UidB);
        var held = target is not null && target.CharacterId == player.CharacterId
            && target.StorageType is StorageBag or StorageEquipped;
        var result = held ? ResultCannotExtend : ResultItemMissing;

        Logger.LogInformation("{Name} previews an extension of {B} (bag {BagB}/slot {SlotB}, item {Item}) with {A} (bag {BagA}/slot {SlotA}): refused {Result}, this server keeps no timed items",
            player.Name, req.UidB, req.BagB, req.SlotB, held ? target!.ItemId : 0u, req.UidA, req.BagA, req.SlotA, result);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPreViewCacheItem, BuildRefusal2846(result));
    }
}
