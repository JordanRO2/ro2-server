using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1454 ReqGuildEmblem - Request guild emblem data (IDA: empty)</summary>
public class GuildEmblemHandler : ZoneRmiHandlerBase<GuildEmblemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildEmblem;
    public override string Name => "GuildEmblem";
    public GuildEmblemHandler(IServiceScopeFactory s, ILogger<GuildEmblemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} requests guild emblem", player.Name);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();

        var guid = new byte[16];
        if (payload.Length >= 16) payload.Span.Slice(0, 16).CopyTo(guid);
        var guildless = Array.TrueForAll(guid, b => b == 0);
        ushort code = guildless ? (ushort)7 : (ushort)0;
        var emblem = guildless ? null : emblems.Get(guid);
        var image = emblem?.Image ?? Array.Empty<byte>();

        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildEmblem, writer =>
        {
            var pos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(code);
            writer.WriteBytes(guid);
            writer.WriteUInt16(emblem?.Index ?? 0);   
            writer.WriteUInt32((uint)image.Length);
            writer.WriteBytes(image);
            writer.WriteAt(pos, (uint)(writer.Length - start));
        });
    }
}
