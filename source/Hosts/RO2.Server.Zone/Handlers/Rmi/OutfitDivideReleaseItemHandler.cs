using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles the release of an appearance-separation scroll (RMI 1701, ReqOutfitDivideReleaseItem).</summary>
/// <remarks>
/// <para>
/// Request: <c>[u64 targetUid][u64 scrollUid]</c>. Answer (3038): <c>[u16 result][u64 scrollUid][126-byte target record]</c>.
/// </para>
/// <para>
/// The release clears the look-source mark on the target. This server never sets that mark, so every target is already
/// releasable and the answer carries the target's current record. The scroll is not consumed, and no 2848 is broadcast
/// because the look does not change. Requests for items the player does not own are dropped silently.
/// </para>
/// </remarks>
public class OutfitDivideReleaseItemHandler : ZoneRmiHandlerBase<OutfitDivideReleaseItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOutfitDivideReleaseItem;
    public override string Name => "OutfitDivideReleaseItem";
    public OutfitDivideReleaseItemHandler(IServiceScopeFactory s, ILogger<OutfitDivideReleaseItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Length of the request body in bytes.</summary>
    internal const int RequestLength = 16;

 /// <summary>Length of the answer body in bytes.</summary>
    internal const int AnswerLength = 2 + 8 + ItemPackets.ItemRecordLength;

 /// <summary>ItemInfo Item_Type of the appearance-separation release scroll.</summary>
    internal const byte ReleaseScrollItemType = 128;

 /// <summary>Reads the target and scroll uids from a request body; false unless the body is exactly 16 bytes.</summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out ulong targetUid, out ulong scrollUid)
    {
        targetUid = scrollUid = 0;
        if (body.Length != RequestLength) return false;
        targetUid = BinaryPrimitives.ReadUInt64LittleEndian(body);
        scrollUid = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        return true;
    }

 /// <summary>Builds the 3038 answer: the result, the scroll uid and the 126-byte target record.</summary>
    internal static byte[] BuildAnswer3038(ushort result, ulong scrollUid, ReadOnlySpan<byte> targetRecord126)
    {
        if (targetRecord126.Length != ItemPackets.ItemRecordLength)
            throw new ArgumentException($"item record must be {ItemPackets.ItemRecordLength} bytes", nameof(targetRecord126));
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), scrollUid);
        targetRecord126.CopyTo(body.AsSpan(10));
        return body;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest(payload.Span, out var targetUid, out var scrollUid))
        {
            Logger.LogWarning("{Name} sent a {Len}-byte ReqOutfitDivideReleaseItem (the builder sends {Want})",
                player.Name, payload.Length, RequestLength);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        var target = await inventory.GetByIdAsync(targetUid);
        var scroll = await inventory.GetByIdAsync(scrollUid);
        if (target is null || scroll is null
            || target.CharacterId != player.CharacterId || scroll.CharacterId != player.CharacterId
            || store.GetItem(scroll.ItemId)?.Item_Type != ReleaseScrollItemType
            || target.StorageType is not (0 or 1))
        {
            Logger.LogInformation("{Name} outfit release dropped: target {Target} / scroll {Scroll} not a valid owned pair",
                player.Name, targetUid, scrollUid);
            return;
        }

        Logger.LogDebug("{Name} releases item {Target} ({ItemId}) with scroll {Scroll} (scroll kept)",
            player.Name, targetUid, target.ItemId, scrollUid);

        var body = BuildAnswer3038(0, scrollUid, WorldEntryService.BuildItemRecord126(target));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOutfitDivideReleaseItem, (ReadOnlyMemory<byte>)body, ct);
    }
}
