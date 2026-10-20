using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1365 ReqSkillAdd (ByteArray). Adds a skill to the skill bar.</summary>
public class SkillAddHandler : ZoneRmiHandlerBase<SkillAddHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillAdd;
    public override string Name => "SkillAdd";

    public SkillAddHandler(IServiceScopeFactory s, ILogger<SkillAddHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 8) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(4); 
        var skillId = reader.ReadUInt32();

        var store = services.GetRequiredService<GameDataStore>();
        var skill = store.Skills.GetValueOrDefault(skillId);

        Logger.LogDebug("{Name} adds skill {SkillId} ({SkillName}) to bar",
            player.Name, skillId, skill?.Name ?? "?");

        
    }
}
