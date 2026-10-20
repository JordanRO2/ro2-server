using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1704 ReqCheatTool: the client reports one of its integrity checks.</summary>
/// <remarks>
/// The body is a length-prefixed byte holding the check counter. No reply is sent; the handler only records the report.
/// </remarks>
public class CheatToolHandler : ZoneRmiHandlerBase<CheatToolHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCheatTool;
    public override string Name => "CheatTool";

    public CheatToolHandler(IServiceScopeFactory s, ILogger<CheatToolHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 5) return Task.CompletedTask;
        var reader = new PacketReader(payload.Span);
        var len = reader.ReadUInt32();
        var tick = len >= 1 ? reader.ReadByte() : (byte)0;

        Logger.LogInformation("{Name} (char {CharId}) anti-cheat report 1704: len={Len} tick={Tick}",
            player.Name, player.CharacterId, len, tick);
        return Task.CompletedTask;
    }
}
