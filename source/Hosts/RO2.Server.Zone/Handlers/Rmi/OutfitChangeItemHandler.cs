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

/// <summary>Handles the shape change request (RMI 1700, ReqOutfitChangeItem), which gives one item the look of another.</summary>
/// <remarks>
/// <para>
/// Request: <c>[u8 checkbox][u64][u64 itemUidA][u64 itemUidB]</c>. Answer (3037): <c>[u16 result 0][u64 itemUidA][u64 itemUidB]</c>.
/// </para>
/// <para>
/// The answer confirms the pair without applying the change: the server keeps no look template and no diamond wallet,
/// so nothing is debited and the look is not stored. No refusal is sent, since no refusal code is known.
/// </para>
/// </remarks>
public class OutfitChangeItemHandler : ZoneRmiHandlerBase<OutfitChangeItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOutfitChangeItem;
    public override string Name => "OutfitChangeItem";

    private const int RequestLength = 25;
    private const int ItemUidAOffset = 9;
    private const int ItemUidBOffset = 17;

    public OutfitChangeItemHandler(IServiceScopeFactory s, ILogger<OutfitChangeItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;
        var span = payload.Span;
        ulong npcHandle = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(1, 8));
        ulong uidA = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ItemUidAOffset, 8));
        ulong uidB = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ItemUidBOffset, 8));

        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var itemA = await inventory.GetByIdAsync(uidA);
        var itemB = await inventory.GetByIdAsync(uidB);
        if (itemA is null || itemB is null || itemA.CharacterId != player.CharacterId || itemB.CharacterId != player.CharacterId)
        {
            Logger.LogInformation("{Name} outfit change refused: uid {A} / {B} not owned", player.Name, uidA, uidB);
            return;
        }

        Logger.LogDebug("{Name} outfit change at NPC {Npc:X}: item {A} ({ItemA}) takes the look of {B} ({ItemB}) (look not stored)",
            player.Name, npcHandle, uidA, itemA.ItemId, uidB, itemB.ItemId);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOutfitChangeItem, writer =>
        {
            writer.WriteUInt16(0);      
            writer.WriteUInt64(uidA);   
            writer.WriteUInt64(uidB);   
        });

        
        
        
        
        
        
        
        
        
        await EquipViewNotifyPackets.BroadcastFullSnapshotAsync(Broadcast, services, player);
    }
}
