using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1581 ReqExtendCacheItem — confirm extending a period item's use-by date with a ticket.</summary>
/// <remarks>
/// <para>
/// <b>Who sends it.</b> Window 159 (UICashExtendWnd). Dropping one item on another stores the
/// 20-byte record in the window, sends it as 1580 ReqPreViewCacheItem and sets a window flag. The OK button
/// then sends the same record as 1581 and clears it. So 1581 is the confirm step after a
/// 1580 preview.
/// </para>
/// <para>
/// <b>Request</b>, NONE family, 20 bytes, from the client's builder alone — no frame of 1580,
/// 1581, 2846 or 2847 is known. Two item
/// descriptors: <c>{u8 bag, u8 slot, u64 uid}</c> of the dragged item at 0 and of the drop
/// target at 10 — the {bag, slot, uid} ×2 shape of 1575 ReqUseDyeItem. The old code
/// read it as five u32s with invented names.
/// </para>
/// <para>
/// <b>Answer: always a refusal, because there is nothing to extend.</b> An item instance here keeps no
/// period (InventoryItem has no expiry field) and DateExtend_Group only maps tickets to durations, so no
/// 2847 AnsExtendCacheItem is fixed 137 bytes (read_side Blob&lt;137&gt;);
/// its client handler reads
/// only the u16 result when it is non-zero and prints a chat line: 1 = 2025
/// "Server System Error", 2 = 2323 "The item does not exist.", 3 or 4 = 10658 "Item cannot extend the
/// period", 5 = 10666, 6 = 10667; window 159 is left alone. Sent: <see cref="ResultNoItem"/> when either uid
/// is not one of the player's items, <see cref="ResultCannotExtend"/> otherwise, as
/// <c>[u16 result][135 zero bytes]</c>. The old 10-byte answer was shorter than the reader's 137, so the
/// client dropped it. (Window 159 only opens on a result-0 2846 answering the 1580 preview. As long as
/// PreViewCacheItemHandler cannot answer 0 — it has no expiry to preview either — this confirm is out of
/// reach in practice; it is answered anyway.)
/// </para>
/// </remarks>
public class ExtendCacheItemHandler : ZoneRmiHandlerBase<ExtendCacheItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqExtendCacheItem;
    public override string Name => "ExtendCacheItem";
    public ExtendCacheItemHandler(IServiceScopeFactory s, ILogger<ExtendCacheItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Request length: two 10-byte descriptors (read_side Blob&lt;20&gt;).</summary>
    internal const int RequestLength = 20;

 /// <summary>One descriptor: <c>{u8 bag, u8 slot, u64 uid}</c>.</summary>
    private const int DescriptorLength = 10;

 /// <summary>2847 length: read_side Blob&lt;137&gt;.</summary>
    internal const int AnswerLength = 137;

 /// <summary>Result 2: string_ui 2323 "The item does not exist."</summary>
    internal const ushort ResultNoItem = 2;

 /// <summary>Result 3: string_ui 10658 "Item cannot extend the period".</summary>
    internal const ushort ResultCannotExtend = 3;

 /// <summary>The uids of the dragged item (@2) and the drop target (@12), or false for a short body.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out ulong draggedUid, out ulong targetUid)
    {
        draggedUid = targetUid = 0;
        if (body.Length < RequestLength) return false;
        draggedUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(2));
        targetUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(DescriptorLength + 2));
        return true;
    }

 /// <summary>The 2847 refusal: <c>[u16 result][135 zero bytes]</c>; the non-zero path reads only the u16.</summary>
    internal static byte[] BuildRefusal(ushort result)
    {
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var draggedUid, out var targetUid))
        {
            Logger.LogWarning("Session {Id}: ReqExtendCacheItem of {Len} bytes, not the 20 the client sends",
                session.SessionId, payload.Length);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var dragged = await inventory.GetByIdAsync(draggedUid);
        var target = await inventory.GetByIdAsync(targetUid);
        var owned = dragged is not null && target is not null
            && dragged.CharacterId == player.CharacterId && target.CharacterId == player.CharacterId;

        var result = owned ? ResultCannotExtend : ResultNoItem;
        Logger.LogInformation("{Name} period extension {Dragged} -> {Target} refused with {Result} ({Why})",
            player.Name, draggedUid, targetUid, result,
            owned ? "no per-item period is kept to extend" : "item not found or not the player's");

        await session.SendRmiAsync(ZoneS2COpcodes.AnsExtendCacheItem, BuildRefusal(result), ct);
    }
}
