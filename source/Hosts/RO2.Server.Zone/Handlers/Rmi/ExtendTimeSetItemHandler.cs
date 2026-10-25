using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1703 ReqExtendTimeSetItem — preview or confirm extending a mount's use-by date with a ticket.</summary>
/// <remarks>
/// <para>
/// <b>Who sends it.</b> Dropping a DurationExtendGroup ticket on a used item (after the client's group-bit test and
/// the item's used flag, else string 10666) sends the record with flag 1 (preview); the OK
/// button sends it again with flag 0 (confirm).
/// </para>
/// <para>
/// <b>Request</b>, NONE family, 17 bytes:
/// <c>[u8 flag][u64 target uid][u64 ticket uid]</c> — the flag at 0 (the client sets 1 for the preview and 0 for
/// the confirm), the drop target's uid (the item whose period is
/// extended, the one the answer's u64 names) at 1, and the dragged ticket's uid
/// at 9.
/// </para>
/// <para>
/// <b>Answer: always a refusal, because there is nothing to extend.</b> An item instance here keeps no
/// period (InventoryItem has no expiry field) and DurationExtendGroup only maps tickets to group bits, so
/// no new use-by date exists to report. 3043 AnsExtendTimeSetItem is fixed 17 bytes (read_side Blob&lt;17&gt;):
/// <c>[u16 result][u8 flag][u64 uid][f32 new period][u16]</c>, which the client's handler reads. Result 0 fills window 159
/// (flag 1) or writes the new
/// period into the item and shows 10556 (flag 0); a non-zero result closes window 159 and shows a
/// notification, reading nothing but the u16: 1 = 15979 "Cannot find the item.", 2 = 15980, 3 = 15981,
/// 4 = 15982 "You cannot use this item.", 5 = 15983, 6 = 10666, others ShowResultCodeMessage. Sent:
/// <see cref="ResultNoItem"/> when either uid is not one of the player's items — retail's answer to an
/// all-zero request is result 1 (nothing reads the remaining bytes on a refusal) — and
/// <see cref="ResultCannotUse"/>
/// otherwise, as <c>[u16 result][15 zero bytes]</c>. The old 2-byte answer was shorter than the reader's 17,
/// so the client dropped it.
/// </para>
/// </remarks>
public class ExtendTimeSetItemHandler : ZoneRmiHandlerBase<ExtendTimeSetItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqExtendTimeSetItem;
    public override string Name => "ExtendTimeSetItem";
    public ExtendTimeSetItemHandler(IServiceScopeFactory s, ILogger<ExtendTimeSetItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Request length: read_side Blob&lt;17&gt; (17 bytes).</summary>
    internal const int RequestLength = 17;

 /// <summary>3043 length: read_side Blob&lt;17&gt; (17 bytes).</summary>
    internal const int AnswerLength = 17;

 /// <summary>3043 result 1, string_ui 15979 "Cannot find the item." (retail's answer to an
 /// all-zero request).</summary>
    internal const ushort ResultNoItem = 1;

 /// <summary>3043 result 4, string_ui 15982 "You cannot use this item."</summary>
    internal const ushort ResultCannotUse = 4;

 /// <summary>The flag (1 preview, 0 confirm), the target's uid (@1) and the ticket's uid (@9), or false
 /// for a short body.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out byte flag, out ulong targetUid, out ulong ticketUid)
    {
        flag = 0;
        targetUid = ticketUid = 0;
        if (body.Length < RequestLength) return false;
        flag = body[0];
        targetUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(1));
        ticketUid = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(9));
        return true;
    }

 /// <summary>The 3043 refusal: <c>[u16 result][15 zero bytes]</c>; the non-zero path reads only the u16.</summary>
    internal static byte[] BuildRefusal(ushort result)
    {
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var flag, out var targetUid, out var ticketUid))
        {
            Logger.LogWarning("Session {Id}: ReqExtendTimeSetItem of {Len} bytes, not the 17 the client sends",
                session.SessionId, payload.Length);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var target = await inventory.GetByIdAsync(targetUid);
        var ticket = await inventory.GetByIdAsync(ticketUid);
        var owned = target is not null && ticket is not null
            && target.CharacterId == player.CharacterId && ticket.CharacterId == player.CharacterId;

        var result = owned ? ResultCannotUse : ResultNoItem;
        Logger.LogInformation("{Name} mount period {Step} with ticket {Ticket} on {Target} refused with {Result} ({Why})",
            player.Name, flag != 0 ? "preview" : "confirm", ticketUid, targetUid, result,
            owned ? "no per-item period is kept to extend" : "item not found or not the player's");

        await session.SendRmiAsync(ZoneS2COpcodes.AnsExtendTimeSetItem, BuildRefusal(result), ct);
    }
}
