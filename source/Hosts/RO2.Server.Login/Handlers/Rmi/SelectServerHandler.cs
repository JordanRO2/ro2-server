using RO2.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Configuration;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Configuration;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12005 - Select Server.
/// The client selects a channel from the server list; the answer layout is
/// [data_length:4][reserved:2 = 0][channel_entry][session_guid:16], where channel_entry is
/// [handle:4][flag:1][name:wstring][zone_host:wstring][zone_port:2][load:1], the entry layout of the 12503 list.
/// </summary>
/// <remarks>
/// The request is the bare u32 handle of the chosen channel (4 bytes), one of the handles the 12503 list
/// gave out. The 12504 answer names that same channel: its handle is the requested one and its name is the
/// one the 12503 list gives that handle.
/// </remarks>
public class SelectServerHandler : LoginRmiHandlerBase<SelectServerHandler>
{
    /// <summary>How long the Zone hand-off token issued with the 12504 answer stays valid (config-driven).</summary>
    private TimeSpan ZoneTokenLifetime => TimeSpan.FromMinutes(_config.Game.ZoneTokenLifetimeMinutes);

    /// <summary>The "any address" bind host; a client cannot connect to it, so loopback is advertised instead.</summary>
    private const string UnspecifiedHost = "0.0.0.0";

    /// <summary>Host advertised to the client when the Zone binds <see cref="UnspecifiedHost"/>.</summary>
    private const string LoopbackHost = "127.0.0.1";

    /// <summary>Size of the CMessage data_length prefix that opens the 12504 body.</summary>
    private const int DataLengthPrefixSize = sizeof(uint);

    /// <summary>The 2 reserved bytes after the data_length prefix; always zero.</summary>
    private const ushort ReservedWord = 0;

    /// <summary>Channel entry flag: the channel is online (same value the 12503 list uses).</summary>
    private const byte ChannelFlagOnline = 0x01;

    /// <summary>Channel entry load indicator, range 0..3; 2 = medium (same value the 12503 list uses).</summary>
    private const byte ChannelLoadMedium = 0x02;

    // ---- Channel handle layout, shared with the 12503 list: (instance << 20) | (type << 14) | (slot << 6) | server index ----

    /// <summary>Bit position of the instance counter in a handle ([31-20]).</summary>
    private const int HandleInstanceShift = 20;

    /// <summary>Bit position of the type field in a handle ([19-14]).</summary>
    private const int HandleTypeShift = 14;

    /// <summary>Bit position of the slot field in a handle ([13-6]).</summary>
    private const int HandleSlotShift = 6;

    /// <summary>Handle type of a channel.</summary>
    private const int HandleTypeChannel = 0x05;

    /// <summary>The instance counter of channel N is this base + N.</summary>
    private const int ChannelInstanceBase = 100;

    private readonly ServerConfiguration _config;

    public override ushort Opcode => LoginC2SOpcodes.SelectServer;
    public override string Name => "SelectServer";

    public SelectServerHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<SelectServerHandler> logger,
        ServerConfiguration config)
        : base(scopeFactory, logger)
    {
        _config = config;
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        // The request is the bare u32 handle; a shorter body is not a 12005.
        if (payload.Length < 4)
        {
            Logger.LogWarning("Session {Id}: SelectServer body is {Size} bytes, expected 4; not answered",
                session.SessionId, payload.Length);
            return;
        }

        var reader = new PacketReader(payload.Span);
        var channelId = reader.ReadUInt32();

        // S-H4: no Zone token without an authenticated account whose PIN this connection verified or created
        // (ClientStateRequestHandler sets the flag). The ack used to go out with a token nobody had issued
        // when there was no account, and with a real one when the PIN step was skipped.
        if (session.AccountId is not uint authedAccount)
        {
            Logger.LogWarning("Session {Id}: SelectServer with no authenticated AccountId; no token issued, not answered",
                session.SessionId);
            return;
        }
        if (!LoginSessionState.For(session).PinVerified)
        {
            Logger.LogWarning("Session {Id}: SelectServer for account {AccountId} before the PIN was verified; "
                + "no token issued, not answered", session.SessionId, authedAccount);
            return;
        }

        // The name 12504 carries: empty for a handle our 12503 never offered, as there is nothing to name it by.
        var channelName = SelectedChannelLabel(_config.World, channelId);

        // S-H8: a handle the list never advertised gets no token and no presence row (before this, both were
        // written for any u32 the client sent).
        if (channelName.Length == 0)
        {
            Logger.LogWarning("Session {Id}: SelectServer names channel 0x{ChannelId:X8}, which the server list "
                + "does not advertise; no token issued, not answered", session.SessionId, channelId);
            return;
        }

        Logger.LogInformation("Session {Id}: Channel selected - ID: 0x{ChannelId:X8} ('{Name}')",
            session.SessionId, channelId, channelName);

        // Record the account's channel for the location of 23505 friend records (ChannelPresenceStore): the
        // Zone turns it into the label of this account's character at 1417 (empty for a handle its list does not
        // have, so an old label never outlives a new selection), and the Chat server shows it to the character's
        // friends. Written before the ack, so it is there when the client reaches the Zone. A storage failure
        // only leaves the location empty or stale; it never blocks the login.
        try
        {
            await services.GetRequiredService<IChannelPresenceStore>()
                .SetAccountChannelAsync(authedAccount, channelId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not record the selected channel 0x{ChannelId:X8}",
                session.SessionId, channelId);
        }

        var sessionGuid = Guid.NewGuid();

        // Bind this handoff GUID to the authenticated account so the Zone can trust it instead of reading
        // AccountId straight from the (spoofable) client packet. See IZoneAuthTokenRepository. Issued BEFORE
        // the ack so the token already exists when the client turns around and connects to the Zone.
        await services.GetRequiredService<RO2.Application.Interfaces.IZoneAuthTokenRepository>()
            .IssueAsync(sessionGuid, authedAccount, ZoneTokenLifetime);

        await session.SendRmiAsync(LoginS2COpcodes.SelectServerAck, writer =>
        {
            // CMessage data_length prefix
            var lengthPos = writer.Reserve(DataLengthPrefixSize);
            var dataStart = writer.Length;

            writer.WriteUInt16(ReservedWord);

            // Channel entry (same format as ServerGroupList)
            writer.WriteUInt32(channelId);
            writer.WriteByte(ChannelFlagOnline);

            // The selected channel's own name, as the 12504 answer carries it (not a fixed 'Channel 1').
            writer.WriteUnicodeString(channelName);

            writer.WriteUnicodeString(ZoneAdvertisedHost());

            writer.WriteUInt16((ushort)_config.Zone.Port);
            writer.WriteByte(ChannelLoadMedium);

            // Session GUID (16 bytes raw) for zone server authentication
            writer.WriteBytes(sessionGuid.ToByteArray());

            // Fill CMessage data_length
            writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
        });

        Logger.LogInformation("Session {Id}: SelectServerAck sent (zone={Host}:{Port}, guid={Guid})",
            session.SessionId,
            ZoneAdvertisedHost(),
            _config.Zone.Port,
            sessionGuid);
    }

    /// <summary>The Zone host the 12504 answer advertises: the configured host, or loopback for the any-address bind.</summary>
    private string ZoneAdvertisedHost()
        => _config.Zone.Host == UnspecifiedHost ? LoopbackHost : _config.Zone.Host;

    /// <summary>
    /// The 12503 name of the channel with handle <paramref name="handle"/> ('Channel N'), or empty when the list
    /// LoginService.SendServerGroupListAsync builds from <paramref name="world"/> has no such handle.
    /// </summary>
    public static string SelectedChannelLabel(WorldConfig world, uint handle)
        => ChannelPresenceStore.LabelOf(handle, world.ChannelCount, ch => AdvertisedHandle(world, ch));

    /// <summary>
    /// The handle LoginService.SendServerGroupListAsync gives channel <paramref name="channel"/>:
    /// (instance 100+N &lt;&lt; 20) | (type 5 &lt;&lt; 14) | (slot N &lt;&lt; 6) | server index, the same expression
    /// as the 12503 list uses.
    /// </summary>
    private static uint AdvertisedHandle(WorldConfig world, int channel)
        => (uint)(((ChannelInstanceBase + channel) << HandleInstanceShift) | (HandleTypeChannel << HandleTypeShift)
            | (channel << HandleSlotShift) | world.ServerIndex);
}
