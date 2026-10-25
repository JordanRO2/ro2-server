using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// 2464 OnMountGliderNotify and 2465 OnUnmountGliderNotify: the glider mount and dismount, sent to the rider
/// and to the players nearby.
/// </summary>
/// <remarks>
/// Both bodies carry the player view record that 2504 sends. The mount copy sets the mount bit (0x0080) with
/// the mount id; the dismount copy re-sends the same record without it.
/// </remarks>
public static class GliderNotifyPackets
{
    /// <summary>2464 OnMountGliderNotify message tag (0xE306), blob+2.</summary>
    internal const ushort MsgTagMountGliderNotify = 58118;

    /// <summary>2465 OnUnmountGliderNotify message tag (0xE309), blob+2.</summary>
    internal const ushort MsgTagUnmountGliderNotify = 58121;

    /// <summary>The reserved u32 at blob+4 of both messages; always zero (it is not the rider handle).</summary>
    private const uint BlobReservedWord = 0;

    /// <summary>Size of the u16 selfLen field at blob+0, which counts itself so it equals the blob length.</summary>
    private const int SelfLenSize = 2;

    /// <summary>Capacity reserved for the compact-length prefix ahead of the blob (a length of 0x80 or more takes 2 bytes).</summary>
    private const int CompactLengthMaxSize = 3;

    /// <summary>2464 body for <paramref name="rider"/>, carrying <paramref name="mountId"/> under mask bit 0x0080.</summary>
    public static byte[] BuildMountGliderNotify(PlayerState rider, Character character,
        IReadOnlyList<InventoryItem>? equipment, uint mountId, IGuildEmblemRepository emblems)
        => Build(MsgTagMountGliderNotify, rider, character, equipment, mountId, emblems);

    /// <summary>2465 body for <paramref name="rider"/>: the same record without the mount bit.</summary>
    public static byte[] BuildUnmountGliderNotify(PlayerState rider, Character character,
        IReadOnlyList<InventoryItem>? equipment, IGuildEmblemRepository emblems)
        => Build(MsgTagUnmountGliderNotify, rider, character, equipment, null, emblems);

    private static byte[] Build(ushort msgTag, PlayerState rider, Character character,
        IReadOnlyList<InventoryItem>? equipment, uint? mountId, IGuildEmblemRepository emblems)
    {
        using var blob = new PacketWriter();
        var selfLenPos = blob.Reserve(SelfLenSize);
        blob.WriteUInt16(msgTag);
        blob.WriteUInt32(BlobReservedWord);   // blob+4
        PlayerSpawnService.WriteRecord(blob, rider, character, emblems, equipment, mountId);
        blob.WriteAt(selfLenPos, (ushort)blob.Length);   // selfLen counts itself: == len(blob)

        // The compact length uses the signed width (0x80..0xFF goes out as 2 bytes), which PacketWriter handles.
        using var w = new PacketWriter(blob.Length + CompactLengthMaxSize);
        w.WriteCompactLength(blob.Length);
        w.WriteBytes(blob.GetWrittenSpan());
        return w.ToArray();
    }

    /// <summary>Sends 2464 to the rider and to every other player on the map, so the rider also gets their own copy.</summary>
    public static Task SendMountAsync(ProudSession riderSession, PlayerState rider, uint mountId,
        IInventoryRepository inventory, IGuildEmblemRepository emblems, BroadcastService broadcast, ILogger logger)
        => SendAsync(ZoneS2COpcodes.OnMountGliderNotify, riderSession, rider, mountId, inventory, emblems, broadcast, logger);

    /// <summary>Sends 2465 to the rider and to every other player on the map, after the rider's 2442.</summary>
    public static Task SendUnmountAsync(ProudSession riderSession, PlayerState rider,
        IInventoryRepository inventory, IGuildEmblemRepository emblems, BroadcastService broadcast, ILogger logger)
        => SendAsync(ZoneS2COpcodes.OnUnmountGliderNotify, riderSession, rider, null, inventory, emblems, broadcast, logger);

    private static async Task SendAsync(ushort opcode, ProudSession riderSession, PlayerState rider, uint? mountId,
        IInventoryRepository inventory, IGuildEmblemRepository emblems, BroadcastService broadcast, ILogger logger)
    {
        // Without the Character snapshot there is no name, class or appearance to put in the record.
        var character = rider.Character;
        if (character == null)
        {
            logger.LogWarning("Glider notify {Op} skipped for '{Name}' (session {Sid}): no Character snapshot.",
                opcode, rider.Name, rider.SessionId);
            return;
        }

        // Worn gear is read live: the record's equipment vector dresses the actor on the receiving client.
        IReadOnlyList<InventoryItem> equipment;
        try
        {
            equipment = await inventory.GetEquipmentAsync(rider.CharacterId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Glider notify {Op} for '{Name}': equipment read failed; sending without gear.",
                opcode, rider.Name);
            equipment = Array.Empty<InventoryItem>();
        }

        var body = mountId.HasValue
            ? BuildMountGliderNotify(rider, character, equipment, mountId.Value, emblems)
            : BuildUnmountGliderNotify(rider, character, equipment, emblems);

        // Unencrypted internal path.
        await riderSession.SendInternalRmiAsync(opcode, body);
        await broadcast.BroadcastToNearbyAsync(rider, opcode, w => w.WriteBytes(body));
    }
}
