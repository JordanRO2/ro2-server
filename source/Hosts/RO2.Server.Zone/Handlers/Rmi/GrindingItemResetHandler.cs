using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1683 ReqGrindingItemReset - re-roll one grinding option of an item with a honing hammer.</summary>
/// <remarks>
/// <para>
/// REQUEST. 17 bytes, no envelope: <c>[u64 materialUid][u64 targetUid][u8 option]</c>.
/// </para>
/// <para>
/// Intended behaviour: the
/// material at @0 is a honing hammer (Precise Honing Hammer 70000948, ItemInfo Item_Type 120);
/// a 2329 OnUseItem on it with result 0 and useSource 1 precedes every 3015; the 3015
/// is <c>[u16 result][126-byte record of the item at @8]</c> = 128 bytes, record uid == request @8.
/// A re-roll changes exactly one 4-byte grinding option
/// at record+75+4*option: option 2 -> record+83..86, option 3 -> record+87..90.
/// </para>
/// <para>
/// NOT IMPLEMENTED, on purpose: this server stores no grinding options - WorldEntryService writes
/// record+75..94 as zeros and no handler ever rolls one - so there is no option to re-roll, and taking the
/// hammer would charge for nothing. Every request is answered with the refusal code 25202 over an
/// all-zero record. Nothing is consumed. When options are stored, this handler must re-roll the chosen one,
/// consume one hammer (2329, useSource 1) and answer result 0 over the updated record. Each option slot at
/// record+75+4*i is {u16 Grinding_Abili.Abili_ID, i16 value}
/// and a reset re-draws both the ability and the value.
/// </para>
/// </remarks>
public class GrindingItemResetHandler : ZoneRmiHandlerBase<GrindingItemResetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGrindingItemReset;
    public override string Name => "GrindingItemReset";

    /// <summary>1683 body length: 17 bytes.</summary>
    private const int RequestBodyLength = 17;

    /// <summary>1683 body offset of the honing-hammer item uid (u64).</summary>
    private const int MaterialUidOffset = 0;

    /// <summary>1683 body offset of the target item uid (u64).</summary>
    private const int TargetUidOffset = 8;

    /// <summary>1683 body offset of the grinding option slot to re-roll (u8).</summary>
    private const int OptionOffset = 16;

    /// <summary>The 3015 refusal result (sent with a zeroed record).</summary>
    private const ushort GrindingResetRefused = 25202;

    public GrindingItemResetHandler(IServiceScopeFactory s, ILogger<GrindingItemResetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: GrindingItemReset body is {Len} bytes, not the expected 17",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        ulong materialUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(MaterialUidOffset));
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(TargetUidOffset));
        byte option = span[OptionOffset];

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        bool owned = target is not null && target.CharacterId == player.CharacterId;

        Logger.LogInformation("{Name} asks to re-roll grinding option {Option} of uid {Uid} ('{Item}', owned={Owned}) with material uid {Mat}: refused, no grinding options are stored",
            player.Name, option, targetUid, owned ? store.GetItem(target!.ItemId)?.Name ?? "?" : "?", owned, materialUid);

        // AnsGrindingItemReset (3015): fixed 128-byte body = [u16 result][126-byte item record].
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGrindingItemReset, writer =>
        {
            writer.WriteUInt16(GrindingResetRefused);         // body+0: result
            writer.WriteZeros(ItemPackets.ItemRecordLength);  // body+2: zeroed item record, as a refusal
        });
    }
}
