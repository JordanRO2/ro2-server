using RO2.Application.Interfaces;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Configuration;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1420 ReqChannelMove: moves the player to another channel.</summary>
/// <remarks>
/// <para>
/// A move is refused with a 2556 result code when the requested channel is the account's current channel
/// (<see cref="ResultCurrentChannel"/>), or when it is not in the channel list this world advertises
/// (<see cref="ResultNoSuchChannel"/>). Result codes without a defined meaning are not emitted.
/// </para>
/// </remarks>
public class ChannelMoveHandler : ZoneRmiHandlerBase<ChannelMoveHandler>
{
 /// <summary>2556 result code: the requested channel is the account's current channel.</summary>
    internal const ushort ResultCurrentChannel = 1018;

 /// <summary>2556 result code: the requested channel does not exist.</summary>
    internal const ushort ResultNoSuchChannel = 1014;

 /// <summary>Host address field of the 2556 body: a wide string of 16 characters at offset 22.</summary>
    private const int TargetHostFieldBytes = 32;

    public override ushort Opcode => ZoneC2SOpcodes.ReqChannelMove;
    public override string Name => "ChannelMove";
    public ChannelMoveHandler(IServiceScopeFactory s, ILogger<ChannelMoveHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<ServerConfiguration>();
        var host = ZoneChannelList.AdvertisedHost(config);
        var port = (ushort)config.Zone.Port;

        
        
        var requestedChannel = payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Span)
            : 0u;

        
        
        
        var accountId = session.AccountId ?? player.AccountId;

        
        
        var currentChannel = await RecordedChannelAsync(services, accountId, ct);
        if (RefusalCode(requestedChannel, currentChannel, config.World) is ushort refusal)
        {
            Logger.LogInformation("{Name} channel move to 0x{Handle:X8} refused with {Code} (current 0x{Current:X8})",
                player.Name, requestedChannel, refusal, currentChannel ?? 0);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsChannelMove, BuildRefusal2556(accountId, refusal), ct);
            return;
        }

        
        
        
        
        
        
        
        var targetChannel = requestedChannel;

        
        
        
        var token = RandomNumberGenerator.GetBytes(ChannelMoveTicketStore.TokenBytes);

        Logger.LogInformation("{Name} requests channel move to handle 0x{Handle:X8}", player.Name, requestedChannel);

        
        
        
        
        
        
        services.GetRequiredService<ChannelMoveTicketStore>()
            .Issue(token, accountId, player.CharacterUid, targetChannel);

        
        
        
        
        
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, Logger, ct);

        
        
        
        
        
        await services.GetRequiredService<PersistenceService>().FlushAsync(player);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, SessionExitPackets.Ack2938); 
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
            PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }));

        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsChannelMove,
            BuildAnsChannelMove2556(accountId, 0, token, host, port, player.CharacterUid), ct);
    }

 /// <summary>
 /// The 2556 body: 81 bytes, with no length prefix.
 /// <code>
 /// 0 u32 accountId
 /// 4 u16 result (0 on a move; 1018, 1014, 1015 or 1021 on a refusal)
 /// 6 byte[16] token
 /// 22 wchar[16] host (zero-terminated)
 /// 54 u16 port (the target channel's listener)
 /// 56 u32 mapId destination override; zero when the body answers a 1420
 /// 60 3x f32 position destination override; zero when the body answers a 1420
 /// 72 u8 destination override
 /// 73 u64 characterUid
 /// </code>
 /// </summary>
    internal static byte[] BuildAnsChannelMove2556(uint accountId, ushort result, ReadOnlySpan<byte> token,
        string host, ushort port, ulong characterUid)
    {
        var body = new byte[DungeonChannelHandOff.AnsChannelMoveBytes];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, accountId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], result);
        token[..Math.Min(token.Length, ChannelMoveTicketStore.TokenBytes)].CopyTo(span[6..]);
        var hostBytes = Encoding.Unicode.GetBytes(host);
        var hostLen = Math.Min(hostBytes.Length, TargetHostFieldBytes - 2); 
        hostBytes.AsSpan(0, hostLen).CopyTo(span[22..]);
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], port);
        
        BinaryPrimitives.WriteUInt64LittleEndian(span[73..], characterUid);
        return body;
    }

 /// <summary>
 /// The 81-byte 2556 that refuses a move: the account at offset 0, <paramref name="result"/> at offset 4, and
 /// zeros after it.
 /// </summary>
    internal static byte[] BuildRefusal2556(uint accountId, ushort result)
        => DungeonEntryGate.BuildRefusal2556(accountId, result); 

 /// <summary>
 /// The 2556 refusal code for a move to <paramref name="requested"/>, or null when the move may proceed.
 /// <paramref name="current"/> is the account's recorded channel, or null when there is none.
 /// </summary>
    internal static ushort? RefusalCode(uint requested, uint? current, WorldConfig world)
    {
        if (current is uint c && c != 0 && requested == c)
            return ResultCurrentChannel;
        for (int ch = 1; ch <= world.ChannelCount; ch++)
            if (ZoneChannelList.ChannelId(world, ch) == requested)
                return null;
        return ResultNoSuchChannel;
    }

 /// <summary>
 /// The channel recorded for <paramref name="accountId"/> by the 12005 selection or the last 1421, or null when
 /// there is none or it cannot be read. A storage failure skips the current-channel check and never blocks a move.
 /// </summary>
    private async Task<uint?> RecordedChannelAsync(IServiceProvider services, uint accountId, CancellationToken ct)
    {
        try
        {
            return await services.GetRequiredService<IChannelPresenceStore>().GetAccountChannelAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Account {Account}: could not read its channel; the 1018 check is skipped", accountId);
            return null;
        }
    }
}
