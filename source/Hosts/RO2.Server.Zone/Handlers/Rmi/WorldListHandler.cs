using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1604 (ReqWorldList): replies with the world/server list.</summary>
public class WorldListHandler : ZoneRmiHandlerBase<WorldListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqWorldList;
    public override string Name => "WorldList";

    public WorldListHandler(IServiceScopeFactory s, ILogger<WorldListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Leading u8 of the AnsWorldList body, always 0.</summary>
    private const byte LeadingByte = 0;

    /// <summary>Number of world entries in the list.</summary>
    private const ushort WorldCount = 1;

    /// <summary>Bytes of one world entry before its name characters: the u32 server id and the u16 name length.</summary>
    private const int EntryHeaderBytes = sizeof(uint) + sizeof(ushort);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // AnsWorldList (2890) body: [u8 leading][VarBlob]. The VarBlob is a u32 length followed by { u16 world
        // count, then per world: u32 server id, u16 name length in characters, UTF-16LE name }. Every length
        // prefix matches its data, so each client read stays in bounds.
        //
        // The world's id is the same composed handle the Login server-group list and the Zone channel list send
        // (ZoneChannelList.ServerId): (ServerInstance << 20) | (type << 14) | (slot << 6) | ServerIndex. With the
        // default world config it is 0x00C10001, whose little-endian bytes are 01 00 C1 00. A retail capture of
        // this frame shows those four bytes as a "world id 0x0001" u16 immediately followed by a "0x00C1" u16;
        // those are not two fields but the low and high halves of this one server id, so we emit it as one u32.
        var world = services.GetRequiredService<ServerConfiguration>().World;
        var worldName = world.ServerName;
        await session.SendRmiAsync(ZoneS2COpcodes.AnsWorldList, writer =>
        {
            var blobLength = sizeof(ushort) + EntryHeaderBytes + worldName.Length * sizeof(char);
            writer.WriteByte(LeadingByte);
            writer.WriteUInt32((uint)blobLength);
            writer.WriteUInt16(WorldCount);
            writer.WriteUInt32(ZoneChannelList.ServerId(world));
            writer.WriteUInt16((ushort)worldName.Length);
            foreach (var ch in worldName)
                writer.WriteUInt16(ch);
        });
    }
}
