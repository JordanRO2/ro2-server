using RO2.Application.Interfaces;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Configuration;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-driven channel hand-off into a dungeon. The player leaves this connection with a 2556 AnsChannelMove
/// whose destination block names the dungeon's arrival point, then reconnects and enters the dungeon map.
/// </summary>
/// <remarks>
/// <para>
/// Sequence: the mover's view-leave notices, the optional 2598 group reset, 2938, a self 2505, and the 81-byte
/// 2556 with result 0. The client then reconnects and the character is loaded on the dungeon map.
/// </para>
/// <para>
/// The destination block carries the map, the position and the direction of the arrival point, taken from
/// Position_Info through DungeonInfo. The arrival is written into the live player state before the 2556, so
/// the reconnect loads the character at the arrival point.
/// </para>
/// <para>
/// This implementation is single-instance: per-party instance copies, entry counts, bind values and the
/// in-combat refusal are not modelled.
/// </para>
/// <para>
/// The 2598 group reset is sent only when <c>leaveP2PGroup</c> is set. Onlookers receive the departure notice
/// before the map changes, as for every other map change.
/// </para>
/// </remarks>
public static class DungeonChannelHandOff
{
 /// <summary>
 /// 2598 NotifyChangeP2PGroupID body: <c>[u32 p2pGroupId]</c>, always 0.
 /// </summary>
    internal static ReadOnlyMemory<byte> LeaveP2PGroup2598 { get; } = new byte[4];

 /// Fixed body length of 2556: 81 bytes, no length prefix.
    public const int AnsChannelMoveBytes = 81;

 /// Size of the host field inside 2556: a 16-character wide string, zero padded.
    private const int HostFieldBytes = 32;

 /// <summary>
 /// Hands <paramref name="player"/> off to <paramref name="arrival"/>: ticket, departure notices, arrival written
 /// into the live state and the database, then 2938, the self 2505 and the 2556 with the destination block. The
 /// caller must have checked that the arrival's map is in Map_List. When <paramref name="leaveP2PGroup"/> is
 /// true, the 2598 reset is sent before 2938.
 /// </summary>
    public static async Task EnterAsync(ProudSession session, PlayerState player, PositionData arrival,
        IServiceProvider services, ILogger logger, CancellationToken ct, bool leaveP2PGroup = false)
    {
        var config = services.GetRequiredService<ServerConfiguration>();
        var host = ZoneChannelList.AdvertisedHost(config);
        var port = (ushort)config.Zone.Port;
        
        
        var accountId = session.AccountId ?? player.AccountId;
        var channel = await CurrentChannelAsync(services, accountId, config.World, logger, ct);

        
        
        var token = RandomNumberGenerator.GetBytes(ChannelMoveTicketStore.TokenBytes);
        services.GetRequiredService<ChannelMoveTicketStore>().Issue(token, accountId, player.CharacterUid, channel);

        
        
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, logger, ct);

        
        await services.GetRequiredService<PlayerSpawnService>().NotifyPlayerDespawnAsync(player);

        
        
        
        player.ClearRevivalOffers();
        player.MapLoaded = false;
        player.MapId = arrival.Area_ID;
        player.X = arrival.Position_X;
        player.Y = arrival.Position_Y;
        player.Z = arrival.Position_Z;
        await services.GetRequiredService<PersistenceService>().FlushAsync(player);

        
        
        
        if (leaveP2PGroup)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NotifyChangeP2PGroupID, LeaveP2PGroup2598);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, SessionExitPackets.Ack2938);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
            PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsChannelMove,
            BuildAnsChannelMove2556(accountId, token, host, port, arrival, player.CharacterUid));

        logger.LogInformation("{Name} handed off to dungeon map {Map} ({X:F1},{Y:F1},{Z:F1}) dir {Dir} over channel 0x{Channel:X8}",
            player.Name, arrival.Area_ID, arrival.Position_X, arrival.Position_Y, arrival.Position_Z, arrival.Direction, channel);
    }

 /// <summary>
 /// Builds the 81-byte 2556 of a dungeon hand-off:
 /// <c>[u32 accountId][u16 0][16 B token][wchar[16] host][u16 port][u32 map @56][3 x f32 @60][u8 direction @72]
 /// [u64 character GID @73]</c>. The destination block is filled from <paramref name="arrival"/>.
 /// </summary>
    internal static byte[] BuildAnsChannelMove2556(uint accountId, ReadOnlySpan<byte> token, string host, ushort port,
        PositionData arrival, ulong characterUid)
    {
        if (token.Length != ChannelMoveTicketStore.TokenBytes)
            throw new ArgumentException($"the 2556 token is {ChannelMoveTicketStore.TokenBytes} bytes", nameof(token));

        var body = new byte[AnsChannelMoveBytes];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, accountId);
        
        token.CopyTo(span[6..]);
        var hostBytes = Encoding.Unicode.GetBytes(host);
        hostBytes.AsSpan(0, Math.Min(hostBytes.Length, HostFieldBytes - 2)).CopyTo(span[22..]); 
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], port);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], arrival.Area_ID);
        BinaryPrimitives.WriteSingleLittleEndian(span[60..], arrival.Position_X);
        BinaryPrimitives.WriteSingleLittleEndian(span[64..], arrival.Position_Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[68..], arrival.Position_Z);
        body[72] = (byte)arrival.Direction;
        BinaryPrimitives.WriteUInt64LittleEndian(span[73..], characterUid);
        return body;
    }

 /// <summary>
 /// Returns the handle of the channel the account is on, which the 1421 of this hand-off echoes in 2557. Falls
 /// back to channel 1 when there is no record or the store cannot be read.
 /// </summary>
    internal static async Task<uint> CurrentChannelAsync(IServiceProvider services, uint accountId, WorldConfig world,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            if (await services.GetRequiredService<IChannelPresenceStore>().GetAccountChannelAsync(accountId, ct) is uint handle && handle != 0)
                return handle;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Account {Account}: could not read its channel; the dungeon hand-off uses channel 1", accountId);
        }
        return ZoneChannelList.ChannelId(world, 1);
    }
}
