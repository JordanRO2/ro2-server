using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Configuration;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Channel identifiers shared by RMI 2555, 2556 and 2557. The channel list matches the server list the login
/// server sends, and each channel handle is derived from the world, the channel number and the index.
/// The client echoes a selected handle back in RMI 1420 ReqChannelMove.
/// </summary>
internal static class ZoneChannelList
{
    // Handle layout: (instance << 20) | (type << 14) | (slot << 6) | index. The same layout the login server's
    // server-group list uses.
    private const int IdInstanceShift = 20;
    private const int IdTypeShift = 14;
    private const int IdSlotShift = 6;

    /// <summary>Handle type of the server group entry.</summary>
    private const int IdTypeServer = 0x04;

    /// <summary>Handle type of a channel entry.</summary>
    private const int IdTypeChannel = 0x05;

    /// <summary>The server group entry has no slot.</summary>
    private const int ServerSlot = 0;

    /// <summary>Instance counter of channel N is this base + N.</summary>
    private const int ChannelInstanceBase = 100;

    public static uint ServerId(WorldConfig world)
        => (uint)((world.ServerInstance << IdInstanceShift) | (IdTypeServer << IdTypeShift) | (ServerSlot << IdSlotShift) | world.ServerIndex);

    public static uint ChannelId(WorldConfig world, int channel)
        => (uint)(((ChannelInstanceBase + channel) << IdInstanceShift) | (IdTypeChannel << IdTypeShift) | (channel << IdSlotShift) | world.ServerIndex);

 /// <summary>Replaces the bind wildcard with an address that clients can reach, as the login list does.</summary>
    public static string AdvertisedHost(ServerConfiguration config)
        => config.Zone.Host == "0.0.0.0" ? "127.0.0.1" : config.Zone.Host;
}

/// <summary>RMI 1419 ReqChannelList: returns the available channels.</summary>
public class ChannelListHandler : ZoneRmiHandlerBase<ChannelListHandler>
{
    /// <summary>The list carries exactly one server group entry.</summary>
    private const ushort ServerGroupCount = 1;

    /// <summary>Channel entry flag: the channel is online (the client draws its load indicator).</summary>
    private const byte ChannelFlagOnline = 0x01;

    /// <summary>Channel load indicator. 0 draws the lowest capacity bar; this server has no per-channel capacity
    /// or population, so every channel reports 0.</summary>
    private const byte ChannelLoadNone = 0x00;

    public override ushort Opcode => ZoneC2SOpcodes.ReqChannelList;
    public override string Name => "ChannelList";
    public ChannelListHandler(IServiceScopeFactory s, ILogger<ChannelListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<ServerConfiguration>();
        var world = config.World;
        var host = ZoneChannelList.AdvertisedHost(config);
        var port = (ushort)config.Zone.Port;

        // RMI 2555 AckChannelListInGame. Body layout:
        //   [u32 dataLength][u16 serverCount][u32 serverId][WStr16 serverName][u16 channelCount]
        //   channelCount x { u32 handle, u8 online, WStr16 name, WStr16 host, u16 port, u8 load }
        await session.SendRmiAsync(ZoneS2COpcodes.AckChannelListInGame, writer =>
        {
            var lengthPos = writer.Reserve(sizeof(uint));
            var dataStart = writer.Length;

            writer.WriteUInt16(ServerGroupCount);
            writer.WriteUInt32(ZoneChannelList.ServerId(world));
            writer.WriteUnicodeString(world.ServerName);

            writer.WriteUInt16((ushort)world.ChannelCount);
            for (int ch = 1; ch <= world.ChannelCount; ch++)
            {
                writer.WriteUInt32(ZoneChannelList.ChannelId(world, ch));
                writer.WriteByte(ChannelFlagOnline);
                writer.WriteUnicodeString($"Channel {ch}");
                writer.WriteUnicodeString(host);
                // Every channel points at the single zone listener.
                writer.WriteUInt16(port);
                writer.WriteByte(ChannelLoadNone);
            }

            writer.WriteAt(lengthPos, (uint)(writer.Length - dataStart));
        });
    }
}
